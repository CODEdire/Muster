using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Muster.Contracts;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Entities.Members;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Musters;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Infrastructure.Commands.Membership;

/// <summary>The previous + new ledger retention day values — returned by <see cref="ConfigCommandService.SetLedgerRetentionAsync"/>
/// so the UI can audit the change without re-reading the guild settings.</summary>
public record LedgerRetentionChange(int OldDays, int NewDays);

/// <summary>
/// Logic for the /config commands that map roles to Discord roles (admin / officer / participant).
/// The guild owner can always run these even before any role is mapped, so the server can be
/// configured without being locked out.
/// </summary>
// musterSettings / questSettings are always supplied by DI; the null-forgiving defaults keep the many
// ConfigCommandService test constructions (which don't touch those setters) from each needing to build one.
public class ConfigCommandService(MusterDbContext db, IOptions<CurrencyRetentionOptions> retention, GuildMusterSettingsService musterSettings = null!, IOptions<TrackingRetentionOptions> trackingRetention = null!, GuildQuestSettingsService questSettings = null!)
{
    /// <summary>Set how many days of detailed ledger history this guild keeps before the prune sweep compacts older
    /// rows into carry-forward checkpoints (0 = inherit the platform default / keep forever). Validated against the
    /// platform cap; the effective window is the smaller of this and the cap.</summary>
    public async Task<CommandResult<LedgerRetentionChange>> SetLedgerRetentionAsync(ulong guildId, int days, CancellationToken ct = default)
    {
        if (days < 0)
        {
            return CommandResult<LedgerRetentionChange>.Error("Retention days can't be negative (0 = inherit the platform default).");
        }

        var cap = retention.Value.MaxLedgerRetentionDays;
        if (LedgerRetention.ExceedsCap(days, cap))
        {
            return CommandResult<LedgerRetentionChange>.Error($"The platform maximum ledger retention is {cap} days.");
        }

        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult<LedgerRetentionChange>.Error("This server isn't set up yet.");
        }

        var settings = guild.Settings;
        var oldDays = settings.LedgerRetentionDays;
        settings.LedgerRetentionDays = days;
        guild.Settings = settings; // reassign so the owned JSON column is detected as changed
        await db.SaveChangesAsync(ct);

        var effective = LedgerRetention.Effective(days, cap);
        var window = effective == 0 ? "unlimited (full history kept)" : $"{effective} days";
        var chosen = days == 0 ? "platform default" : $"{days} days";
        return CommandResult<LedgerRetentionChange>.Ok(
            new LedgerRetentionChange(oldDays, days),
            $"Ledger retention set to {chosen} — effective window: {window}.");
    }

    /// <summary>Load (or seed from entity defaults) the guild's <see cref="GuildTrackingSettings"/> row, apply
    /// <paramref name="mutate"/>, and save. Returns false when the guild doesn't exist yet (no row to attach).</summary>
    private async Task<bool> UpsertTrackingAsync(ulong guildId, Action<GuildTrackingSettings> mutate, CancellationToken ct)
    {
        var row = await db.GuildTrackingSettings.FindAsync([guildId], ct);
        if (row is null)
        {
            if (await db.FindGuildAsync(guildId, ct) is null) { return false; }
            row = new GuildTrackingSettings { GuildId = guildId };
            db.GuildTrackingSettings.Add(row);
        }

        mutate(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Set the guild's background-tracking consent default: opt-in (members must opt in) vs opt-out (on by default).</summary>
    public async Task<CommandResult> SetBackgroundOptInAsync(ulong guildId, bool optIn, CancellationToken ct = default)
    {
        if (!await UpsertTrackingAsync(guildId, t => t.BackgroundTrackingOptIn = optIn, ct))
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        return CommandResult.Ok(optIn
            ? "Background tracking is now **opt-in** — members aren't passively tracked until they run `/track-privacy` and opt in."
            : "Background tracking is now **on by default** — members may opt out with `/track-privacy`. Sessions/events are unaffected.");
    }

    /// <summary>Set which spendable currency a Session mints on close and the minutes-per-coin rate. A blank code or
    /// 0 minutes disables session coin minting.</summary>
    public async Task<CommandResult> SetSessionCoinAsync(ulong guildId, string? currencyCode, int minutesPerCoin, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        if (minutesPerCoin < 0)
        {
            return CommandResult.Error("Minutes-per-coin can't be negative (0 disables session coin minting).");
        }

        var code = string.IsNullOrWhiteSpace(currencyCode) ? null : currencyCode.Trim().ToUpperInvariant();

        // Disable when no currency or no rate.
        if (code is null || minutesPerCoin == 0)
        {
            await UpsertTrackingAsync(guildId, t => { t.SessionCoinCurrencyCode = null; t.MinutesPerCoin = 0; }, ct);
            return CommandResult.Ok("Session coin minting is **off**.");
        }

        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return CommandResult.Error($"No currency with code `{code}` exists. Create it first.");
        }

        if (!currency.IsSpendable)
        {
            return CommandResult.Error($"`{code}` isn't a spendable currency — pick a spendable one for session payouts.");
        }

        await UpsertTrackingAsync(guildId, t => { t.SessionCoinCurrencyCode = code; t.MinutesPerCoin = minutesPerCoin; }, ct);

        return CommandResult.Ok($"Sessions will mint **1 {code}** per **{minutesPerCoin}** eligible minute(s) on close.");
    }

    /// <summary>Set the guild's default anti-AFK guards per tracking lane (background / manual session / scheduled
    /// event). A null lane is left unchanged. These are the baseline a monitored channel (and a manual session open)
    /// may override.</summary>
    public async Task<CommandResult> SetDefaultGuardsAsync(
        ulong guildId, AfkGuards? background = null, AfkGuards? session = null, AfkGuards? events = null,
        CancellationToken ct = default)
    {
        var ok = await UpsertTrackingAsync(guildId, t =>
        {
            if (background is { } bg) { t.DefaultBackgroundGuards = bg; }
            if (session is { } se) { t.DefaultSessionGuards = se; }
            if (events is { } ev) { t.DefaultEventGuards = ev; }
        }, ct);

        return ok ? CommandResult.Ok("Default tracking guards updated.") : CommandResult.Error("This server isn't set up yet.");
    }

    /// <summary>Set the auto-close cap (hours) for a never-stopped session. 0 = never auto-close; null = inherit the
    /// server default.</summary>
    public async Task<CommandResult> SetMaxSessionHoursAsync(ulong guildId, int? hours, CancellationToken ct = default)
    {
        if (hours is < 0)
        {
            return CommandResult.Error("Max session hours can't be negative (0 = never auto-close).");
        }

        if (!await UpsertTrackingAsync(guildId, t => t.MaxSessionHours = hours, ct))
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        return CommandResult.Ok(hours is null
            ? "Max session hours uses the server default."
            : hours == 0
                ? "Sessions never auto-close (stop them manually)."
                : $"Sessions auto-close after {hours} hour(s).");
    }

    /// <summary>Set the minimum seconds a member must accrue in a session to stay on its roster. 0 = keep everyone;
    /// null = inherit the server default.</summary>
    public async Task<CommandResult> SetMinTrackedSecondsAsync(ulong guildId, int? seconds, CancellationToken ct = default)
    {
        if (seconds is < 0)
        {
            return CommandResult.Error("Minimum tracked seconds can't be negative (0 = keep everyone).");
        }

        if (!await UpsertTrackingAsync(guildId, t => t.MinTrackedSeconds = seconds, ct))
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        return CommandResult.Ok(seconds is null
            ? "Minimum tracked seconds uses the server default."
            : seconds == 0
                ? "Drive-by filtering off — every attendee is kept."
                : $"Members who accrue under {seconds}s in a session are dropped from its roster.");
    }

    /// <summary>Set how many days of raw activity records to keep. 0 = keep forever; null = inherit the server
    /// default. Capped by the platform maximum (<c>Tracking:MaxActivityRetentionDays</c>) when one is configured.</summary>
    public async Task<CommandResult> SetActivityRetentionAsync(ulong guildId, int? days, CancellationToken ct = default)
    {
        if (days is < 0)
        {
            return CommandResult.Error("Retention days can't be negative (0 = keep forever).");
        }

        // Platform ceiling: 0 = no limit. A guild can't keep raw activity longer than the configured maximum.
        var cap = trackingRetention?.Value.MaxActivityRetentionDays ?? 0;
        if (days is { } d && cap > 0 && d > cap)
        {
            return CommandResult.Error($"The platform maximum activity retention is {cap} day(s).");
        }

        if (!await UpsertTrackingAsync(guildId, t => t.ActivityRetentionDays = days, ct))
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        return CommandResult.Ok(days is null
            ? "Activity retention uses the server default."
            : days == 0
                ? "Raw activity records are kept indefinitely (rollups always persist)."
                : $"Raw activity records older than {days} day(s) will be pruned (rollups kept).");
    }

    /// <summary>Set the reward-multiplier stacking policy + global cap and the session start/end presence bonuses
    /// (amounts, qualifying-window minutes, and whether the active multiplier scales them). Cap null = inherit default.</summary>
    public async Task<CommandResult> SetMultiplierSettingsAsync(
        ulong guildId, MultiplierStacking stacking, decimal? cap,
        int startBonus, int endBonus, int startWindowMinutes, int endWindowMinutes, bool multiplyBonuses,
        CancellationToken ct = default)
    {
        if (cap is < 0m || startBonus < 0 || endBonus < 0 || startWindowMinutes < 0 || endWindowMinutes < 0)
        {
            return CommandResult.Error("Cap, bonuses, and windows can't be negative (0 = off / no cap).");
        }

        var ok = await UpsertTrackingAsync(guildId, t =>
        {
            t.MultiplierStacking = stacking;
            t.MultiplierCap = cap;
            t.SessionStartBonus = startBonus;
            t.SessionEndBonus = endBonus;
            t.StartBonusWindowMinutes = startWindowMinutes;
            t.EndBonusWindowMinutes = endWindowMinutes;
            t.MultiplyPresenceBonuses = multiplyBonuses;
        }, ct);

        return ok ? CommandResult.Ok("Multiplier & bonus settings updated.") : CommandResult.Error("This server isn't set up yet.");
    }

    public Task<CommandResult> ToggleAdminRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, GuildRoleTier.Admin, ct);

    public Task<CommandResult> ToggleParticipantRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, GuildRoleTier.Participant, ct);

    public Task<CommandResult> ToggleQuestManagerRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, GuildRoleTier.QuestManager, ct);

    public Task<CommandResult> ToggleEconomyManagerRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, GuildRoleTier.EconomyManager, ct);

    public Task<CommandResult> ToggleTrackingManagerRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, GuildRoleTier.TrackingManager, ct);

    public Task<CommandResult> ToggleMusterCreatorRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, GuildRoleTier.MusterCreator, ct);

    public Task<CommandResult> ToggleAuditorRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, GuildRoleTier.Auditor, ct);

    public Task<CommandResult> ToggleShopCreatorRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, GuildRoleTier.ShopCreator, ct);

    public Task<CommandResult> ToggleShopManagerRoleAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
        => ToggleAsync(guildId, roleId, GuildRoleTier.ShopManager, ct);

    /// <summary>Configure the personal-quest approval workflow (intake gate + final sign-off policy).</summary>
    public async Task<CommandResult> SetQuestApprovalAsync(
        ulong guildId, bool intakeApproval, FinalApprovalMode finalMode, bool allowSelfParticipation, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        await questSettings.UpsertAsync(guildId, q =>
        {
            q.PersonalQuestIntakeApproval = intakeApproval;
            q.FinalApprovalMode = finalMode;
            q.AllowSelfParticipation = allowSelfParticipation;
        }, ct);

        return CommandResult.Ok("Quest approval settings updated.");
    }

    /// <summary>Configure all guild muster settings in one save: card channel, terminal-card retention, auto-create
    /// on session + its gate mode, the global reward defaults (points/coins/coin currency), and the optional
    /// allow-list of channels musters may post to (empty = any chat-capable channel).</summary>
    public async Task<CommandResult> SetMusterSettingsAsync(
        ulong guildId, ulong channelId, int retentionHours, bool autoCreate, SessionCoinGate autoCreateGate,
        long defaultPoints, long defaultCoins, Guid? defaultCoinCurrencyId,
        IReadOnlyList<ulong>? allowedChannelIds = null, bool creatorAutoCheckIn = true, int defaultExpiryHours = 0,
        MusterAutoCreateChannel autoCreateChannel = MusterAutoCreateChannel.DefaultChannel, int? defaultMinCheckIns = null,
        MusterResolveMode defaultResolveMode = MusterResolveMode.Pay,
        CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        if (retentionHours < 0 || defaultPoints < 0 || defaultCoins < 0 || defaultExpiryHours < 0 || defaultMinCheckIns is < 0)
        {
            return CommandResult.Error("Retention, expiry, points, coins, and minimum check-ins can't be negative.");
        }

        // A default coin reward needs a spendable currency that belongs to this guild.
        if (defaultCoins > 0 && (defaultCoinCurrencyId is not { } cc
            || !await db.Currencies.AnyAsync(c => c.Id == cc && c.GuildId == guildId && c.IsSpendable, ct)))
        {
            return CommandResult.Error("Pick a spendable currency for the default coin reward.");
        }

        var allowed = (allowedChannelIds ?? []).Where(c => c != 0).Distinct().ToList();

        // A configured default channel must itself be on the allow-list (when one is set), or musters couldn't post there.
        if (channelId != 0 && allowed.Count > 0 && !allowed.Contains(channelId))
        {
            return CommandResult.Error("The default channel must be one of the allowed channels.");
        }

        await musterSettings.UpsertAsync(guildId, s =>
        {
            s.MusterChannelId = channelId;
            s.BoardRetentionHours = retentionHours;
            s.AllowedChannelIds = allowed;
            s.AutoCreateOnSession = autoCreate;
            s.AutoCreateGate = autoCreateGate;
            s.AutoCreateChannel = autoCreateChannel;
            s.CreatorAutoCheckIn = creatorAutoCheckIn;
            s.DefaultExpiryHours = defaultExpiryHours;
            s.DefaultResolveMode = defaultResolveMode;
            s.DefaultMinCheckIns = defaultMinCheckIns;
            s.DefaultPoints = defaultPoints;
            s.DefaultCoins = defaultCoins;
            s.DefaultCoinCurrencyId = defaultCoins > 0 ? defaultCoinCurrencyId : null;
        }, ct);

        return CommandResult.Ok("Muster settings updated.");
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

        var q = await questSettings.UpsertAsync(guildId, s =>
        {
            if (channelId is { } pub)
            {
                s.QuestChannelId = pub;
            }

            if (modChannelId is { } mod)
            {
                s.QuestModChannelId = mod;
            }

            if (retentionHours is { } hours)
            {
                s.BoardRetentionHours = hours;
            }
        }, ct);

        var pubPart = q.QuestChannelId == 0 ? "Public board off (pull-only)" : $"Public board → <#{q.QuestChannelId}>";
        var modPart = q.QuestModChannelId == 0 ? "no mod channel" : $"mod states → <#{q.QuestModChannelId}>";
        return CommandResult.Ok($"{pubPart}; {modPart}; completed cards linger {q.BoardRetentionHours}h.");
    }

    /// <summary>Set whether opening a tracking session auto-creates + links a check-in muster (gating the session
    /// coin). A per-session override still applies at open time.</summary>
    public async Task<CommandResult> SetAutoCreateMusterAsync(ulong guildId, bool enabled, CancellationToken ct = default)
    {
        if (await db.FindGuildAsync(guildId, ct) is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        await musterSettings.UpsertAsync(guildId, s => s.AutoCreateOnSession = enabled, ct);

        return CommandResult.Ok(enabled
            ? "New sessions will auto-post a check-in muster and gate their coin on it (mode Any)."
            : "Sessions won't auto-create a muster (link one manually to gate a session's coin).");
    }

    /// <summary>Point the muster card board at a channel (0 clears it, so musters post to the channel they're
    /// created from). <paramref name="retentionHours"/> null = leave the linger window unchanged.</summary>
    public async Task<CommandResult> SetMusterChannelAsync(ulong guildId, ulong channelId, int? retentionHours = null, CancellationToken ct = default)
    {
        if (await db.FindGuildAsync(guildId, ct) is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        if (retentionHours is < 0)
        {
            return CommandResult.Error("Retention hours can't be negative (0 = delete as soon as terminal).");
        }

        var saved = await musterSettings.UpsertAsync(guildId, s =>
        {
            s.MusterChannelId = channelId;
            if (retentionHours is { } hours)
            {
                s.BoardRetentionHours = hours;
            }
        }, ct);

        return CommandResult.Ok(channelId == 0
            ? "Musters will post to the channel they're created from."
            : $"Muster cards will post to <#{channelId}> (terminal cards linger {saved.BoardRetentionHours}h).");
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

        await questSettings.UpsertAsync(guildId, s =>
        {
            s.IntakeTimeoutHours = intakeHours;
            s.IntakeTimeoutAction = intakeAction;
            s.ClaimTimeoutHours = claimHours;
            s.SubmissionTimeoutHours = submissionHours;
            s.SubmissionTimeoutAction = submissionAction;
            s.FinalApprovalTimeoutHours = finalHours;
            s.FinalApprovalTimeoutAction = finalAction;
            s.MaxOpenQuestsPerPoster = maxOpenPerPoster;
            s.MaxActiveClaimsPerUser = maxActiveClaims;
            s.MaxRevisions = maxRevisions;
            s.DeadlineReminderHours = deadlineReminderHours;
        }, ct);

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

        await questSettings.UpsertAsync(guildId, q =>
        {
            q.TierSPoints = s;
            q.TierAPoints = a;
            q.TierBPoints = b;
            q.TierCPoints = c;
            q.TierDPoints = d;
            q.TierEPoints = e;
        }, ct);

        return CommandResult.Ok("Quest tier points updated.");
    }

    public async Task<CommandResult> ShowAsync(ulong guildId, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var map = await db.RoleMapAsync(guildId, ct);

        string roles(GuildRoleTier tier)
        {
            var ids = map.Where(kv => kv.Value.HasFlag(tier)).Select(kv => kv.Key).ToList();
            return ids.Count == 0 ? "_none_" : string.Join(", ", ids.Select(r => $"<@&{r}>"));
        }

        var participants = map.Values.Any(t => t.HasFlag(GuildRoleTier.Participant))
            ? roles(GuildRoleTier.Participant)
            : "_everyone (open)_";

        return CommandResult.Ok(
            $"**Role mapping**\nAdmin roles: {roles(GuildRoleTier.Admin)}\n" +
            $"Economy manager roles: {roles(GuildRoleTier.EconomyManager)}\n" +
            $"Tracking manager roles: {roles(GuildRoleTier.TrackingManager)}\n" +
            $"Quest manager roles: {roles(GuildRoleTier.QuestManager)}\n" +
            $"Muster creator roles: {roles(GuildRoleTier.MusterCreator)}\n" +
            $"Auditor roles (read-only): {roles(GuildRoleTier.Auditor)}\n" +
            $"Participant roles: {participants}\nGuild owner always has admin access.");
    }

    /// <summary>Flip one tier bit on a role's mapping row: add it if absent, clear it if present. The row is
    /// created on first grant and deleted when its last bit clears, so the table only holds live grants.</summary>
    private async Task<CommandResult> ToggleAsync(ulong guildId, ulong roleId, GuildRoleTier tier, CancellationToken ct)
    {
        if (await db.FindGuildAsync(guildId, ct) is null)
        {
            return CommandResult.Error("This server isn't set up yet.");
        }

        var mapping = await db.FindRoleMappingAsync(guildId, roleId, ct);
        var added = mapping is null || !mapping.Tiers.HasFlag(tier);

        if (added)
        {
            if (mapping is null)
            {
                db.GuildRoleMappings.Add(new GuildRoleMapping { GuildId = guildId, RoleId = roleId, Tiers = tier });
            }
            else
            {
                mapping.Tiers |= tier;
            }
        }
        else
        {
            mapping!.Tiers &= ~tier;
            if (mapping.Tiers == GuildRoleTier.None)
            {
                db.GuildRoleMappings.Remove(mapping);
            }
        }

        await db.SaveChangesAsync(ct);

        var label = Label(tier);
        var note = string.Empty;
        if (tier == GuildRoleTier.Participant && !added
            && !(await db.RoleMapAsync(guildId, ct)).Values.Any(t => t.HasFlag(GuildRoleTier.Participant)))
        {
            note = " Participation is now open to everyone.";
        }

        return CommandResult.Ok((added
            ? $"Added <@&{roleId}> as a {label} role."
            : $"Removed <@&{roleId}> from {label} roles.") + note);
    }

    private static string Label(GuildRoleTier tier) => tier switch
    {
        GuildRoleTier.Admin => "admin",
        GuildRoleTier.EconomyManager => "economy manager",
        GuildRoleTier.TrackingManager => "tracking manager",
        GuildRoleTier.QuestManager => "quest manager",
        GuildRoleTier.MusterCreator => "muster creator",
        GuildRoleTier.Auditor => "auditor",
        _ => "participant",
    };
}
