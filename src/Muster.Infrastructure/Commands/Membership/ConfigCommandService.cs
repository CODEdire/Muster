using Microsoft.Extensions.Options;
using Muster.Contracts;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;

namespace Muster.Infrastructure.Commands.Membership;

public enum RoleKind
{
    Admin,
    Officer,
    Participant,
    QuestManager,
}

/// <summary>
/// Logic for the /config commands that map roles to Discord roles (admin / officer / participant).
/// The guild owner can always run these even before any role is mapped, so the server can be
/// configured without being locked out.
/// </summary>
public class ConfigCommandService(MusterDbContext db, IOptions<CurrencyRetentionOptions> retention)
{
    /// <summary>Set how many days of detailed ledger history this guild keeps before the prune sweep compacts older
    /// rows into carry-forward checkpoints (0 = inherit the platform default / keep forever). Validated against the
    /// platform cap; the effective window is the smaller of this and the cap.</summary>
    public async Task<CommandResult> SetLedgerRetentionAsync(ulong guildId, int days, CancellationToken ct = default)
    {
        if (days < 0)
        {
            return CommandResult.Error("Retention days can't be negative (0 = inherit the platform default).");
        }

        var cap = retention.Value.MaxLedgerRetentionDays;
        if (LedgerRetention.ExceedsCap(days, cap))
        {
            return CommandResult.Error($"The platform maximum ledger retention is {cap} days.");
        }

        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var settings = guild.Settings;
        settings.LedgerRetentionDays = days;
        guild.Settings = settings; // reassign so the owned JSON column is detected as changed
        await db.SaveChangesAsync(ct);

        var effective = LedgerRetention.Effective(days, cap);
        var window = effective == 0 ? "unlimited (full history kept)" : $"{effective} days";
        var chosen = days == 0 ? "platform default" : $"{days} days";
        return CommandResult.Ok($"Ledger retention set to {chosen} — effective window: {window}.");
    }

    /// <summary>Set the guild's background-tracking consent default: opt-in (members must opt in) vs opt-out (on by default).</summary>
    public async Task<CommandResult> SetBackgroundOptInAsync(ulong guildId, bool optIn, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var settings = guild.Settings;
        settings.BackgroundTrackingOptIn = optIn;
        guild.Settings = settings; // reassign so the owned JSON column is detected as changed
        await db.SaveChangesAsync(ct);

        return CommandResult.Ok(optIn
            ? "Background tracking is now **opt-in** — members aren't passively tracked until they run `/track-privacy` and opt in."
            : "Background tracking is now **on by default** — members may opt out with `/track-privacy`. Sessions/events are unaffected.");
    }

    public Task<CommandResult> ToggleAdminRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, RoleKind.Admin, ct);

    public Task<CommandResult> ToggleOfficerRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, RoleKind.Officer, ct);

    public Task<CommandResult> ToggleParticipantRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, RoleKind.Participant, ct);

    public Task<CommandResult> ToggleQuestManagerRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, RoleKind.QuestManager, ct);

    /// <summary>Configure the personal-quest approval workflow (intake gate + final sign-off policy).</summary>
    public async Task<CommandResult> SetQuestApprovalAsync(
        ulong guildId, bool intakeApproval, FinalApprovalMode finalMode, bool allowSelfParticipation, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var settings = guild.Settings;
        settings.Quests.PersonalQuestIntakeApproval = intakeApproval;
        settings.Quests.FinalApprovalMode = finalMode;
        settings.Quests.AllowSelfParticipation = allowSelfParticipation;
        guild.Settings = settings; // reassign so the owned JSON column is detected as changed

        await db.SaveChangesAsync(ct);
        return CommandResult.Ok("Quest approval settings updated.");
    }

    /// <summary>Point the public quest board at a channel (0 clears it, leaving the board pull-only).</summary>
    public Task<CommandResult> SetQuestChannelAsync(ulong guildId, ulong channelId, CancellationToken ct = default)
        => SetQuestBoardAsync(guildId, channelId, modChannelId: null, retentionHours: null, ct);

    /// <summary>Point the mod-only quest states (intake/dispute/final) at a private staff channel (0 clears it).</summary>
    public Task<CommandResult> SetQuestModChannelAsync(ulong guildId, ulong modChannelId, CancellationToken ct = default)
        => SetQuestBoardAsync(guildId, channelId: null, modChannelId, retentionHours: null, ct);

    /// <summary>Configure the live quest board. Each argument is null = leave unchanged; for the channels, a non-null
    /// 0 clears that channel. <paramref name="channelId"/> = public board, <paramref name="modChannelId"/> = private
    /// staff channel for mod-only states, <paramref name="retentionHours"/> = how long completed cards linger.</summary>
    public async Task<CommandResult> SetQuestBoardAsync(
        ulong guildId, ulong? channelId, ulong? modChannelId, int? retentionHours, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        if (retentionHours is < 0)
        {
            return CommandResult.Error("Retention hours can't be negative (0 = delete as soon as completed).");
        }

        var settings = guild.Settings;
        if (channelId is { } pub)
        {
            settings.Quests.QuestChannelId = pub;
        }

        if (modChannelId is { } mod)
        {
            settings.Quests.QuestModChannelId = mod;
        }

        if (retentionHours is { } hours)
        {
            settings.Quests.BoardRetentionHours = hours;
        }

        guild.Settings = settings; // reassign so the owned JSON column is detected as changed

        await db.SaveChangesAsync(ct);

        var q = settings.Quests;
        var pubPart = q.QuestChannelId == 0 ? "Public board off (pull-only)" : $"Public board → <#{q.QuestChannelId}>";
        var modPart = q.QuestModChannelId == 0 ? "no mod channel" : $"mod states → <#{q.QuestModChannelId}>";
        return CommandResult.Ok($"{pubPart}; {modPart}; completed cards linger {q.BoardRetentionHours}h.");
    }

    /// <summary>Configure anti-staleness auto-resolve timeouts and per-player quest limits.</summary>
    public async Task<CommandResult> SetQuestAutomationAsync(
        ulong guildId,
        int intakeHours, StaleIntakeAction intakeAction,
        int claimHours,
        int submissionHours, StaleSubmissionAction submissionAction,
        int finalHours, StaleFinalAction finalAction,
        int maxOpenPerPoster, int maxActiveClaims, int maxRevisions,
        int deadlineReminderHours,
        CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        if (new[] { intakeHours, claimHours, submissionHours, finalHours, maxOpenPerPoster, maxActiveClaims, maxRevisions, deadlineReminderHours }.Any(v => v < 0))
        {
            return CommandResult.Error("Timeouts and limits can't be negative (0 disables).");
        }

        var s = guild.Settings;
        s.Quests.IntakeTimeoutHours = intakeHours;
        s.Quests.IntakeTimeoutAction = intakeAction;
        s.Quests.ClaimTimeoutHours = claimHours;
        s.Quests.SubmissionTimeoutHours = submissionHours;
        s.Quests.SubmissionTimeoutAction = submissionAction;
        s.Quests.FinalApprovalTimeoutHours = finalHours;
        s.Quests.FinalApprovalTimeoutAction = finalAction;
        s.Quests.MaxOpenQuestsPerPoster = maxOpenPerPoster;
        s.Quests.MaxActiveClaimsPerUser = maxActiveClaims;
        s.Quests.MaxRevisions = maxRevisions;
        s.Quests.DeadlineReminderHours = deadlineReminderHours;
        guild.Settings = s; // reassign so the owned JSON column is detected as changed

        await db.SaveChangesAsync(ct);
        return CommandResult.Ok("Quest automation settings updated.");
    }

    /// <summary>Set the bonus POINTS granted per guild-quest difficulty tier.</summary>
    public async Task<CommandResult> SetQuestTierPointsAsync(
        ulong guildId, long s, long a, long b, long c, long d, long e, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        if (new[] { s, a, b, c, d, e }.Any(v => v < 0))
        {
            return CommandResult.Error("Tier points can't be negative.");
        }

        // Reassign Settings so the owned JSON column is detected as changed.
        var settings = guild.Settings;
        settings.Quests.TierSPoints = s;
        settings.Quests.TierAPoints = a;
        settings.Quests.TierBPoints = b;
        settings.Quests.TierCPoints = c;
        settings.Quests.TierDPoints = d;
        settings.Quests.TierEPoints = e;
        guild.Settings = settings;

        await db.SaveChangesAsync(ct);
        return CommandResult.Ok("Quest tier points updated.");
    }

    public async Task<CommandResult> ShowAsync(ulong guildId, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var participants = guild.Settings.ParticipantRoleIds.Count == 0
            ? "_everyone (open)_"
            : Format(guild.Settings.ParticipantRoleIds);

        return CommandResult.Ok(
            $"**Role mapping**\nAdmin roles: {Format(guild.Settings.AdminRoleIds)}\n" +
            $"Officer roles: {Format(guild.Settings.OfficerRoleIds)}\n" +
            $"Participant roles: {participants}\nGuild owner always has admin access.");
    }

    private async Task<CommandResult> ToggleAsync(ulong guildId, ulong roleId, RoleKind kind, CancellationToken ct)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var current = kind switch
        {
            RoleKind.Admin => guild.Settings.AdminRoleIds,
            RoleKind.Officer => guild.Settings.OfficerRoleIds,
            RoleKind.QuestManager => guild.Settings.QuestManagerRoleIds,
            _ => guild.Settings.ParticipantRoleIds,
        };

        var updated = new List<ulong>(current);
        var added = !updated.Remove(roleId);
        if (added)
        {
            updated.Add(roleId);
        }

        // Reassign to ensure the owned JSON column is detected as changed.
        switch (kind)
        {
            case RoleKind.Admin: guild.Settings.AdminRoleIds = updated; break;
            case RoleKind.Officer: guild.Settings.OfficerRoleIds = updated; break;
            case RoleKind.QuestManager: guild.Settings.QuestManagerRoleIds = updated; break;
            default: guild.Settings.ParticipantRoleIds = updated; break;
        }

        await db.SaveChangesAsync(ct);

        var label = kind.ToString().ToLowerInvariant();
        var note = kind == RoleKind.Participant && updated.Count == 0
            ? " Participation is now open to everyone."
            : string.Empty;

        return CommandResult.Ok((added
            ? $"Added <@&{roleId}> as a {label} role."
            : $"Removed <@&{roleId}> from {label} roles.") + note);
    }

    private static string Format(List<ulong> roleIds)
        => roleIds.Count == 0 ? "_none_" : string.Join(", ", roleIds.Select(r => $"<@&{r}>"));
}
