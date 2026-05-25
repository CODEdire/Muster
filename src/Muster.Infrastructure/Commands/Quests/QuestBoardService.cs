using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;

namespace Muster.Infrastructure.Commands.Quests;

/// <summary>Whether a quest is funded by the guild (minted) or by the poster's own balance (escrowed).</summary>
public enum QuestKind
{
    Guild,
    Personal,
}

/// <summary>
/// Single entry point for the unified quest board. Guild quests (minted) and personal quests
/// (escrowed bounties) share one board and one command surface; this service routes each action to
/// the right underlying mechanic based on the quest's origin, and parses the dates a user types in
/// their own time zone.
/// </summary>
public class QuestBoardService(
    MusterDbContext db,
    MissionService missions,
    QuestCommandService quests,
    BountyCommandService bounties,
    GuildAuthorizationService auth,
    TimeZoneService timeZones)
{
    /// <summary>Post a quest with dates already in UTC (used by the web, which converts the browser's local time).</summary>
    public async Task<CommandResult> PostAsync(
        ulong guildId, ulong actorId, QuestKind kind, string name, string currency, long reward,
        string description = "", DateTimeOffset? startsAt = null, DateTimeOffset? deadline = null,
        QuestTier tier = QuestTier.None, bool requestFinalApproval = false, bool repeatable = false, int capacity = 1, CancellationToken ct = default)
    {
        if (deadline is { } dl && startsAt is { } st && dl <= st)
        {
            return CommandResult.Error("The expiry must be after the start time.");
        }

        var settings = await SettingsAsync(guildId, ct);
        if (settings.MaxOpenQuestsPerPoster > 0)
        {
            var openByPoster = await db.Missions.CountAsync(m => m.GuildId == guildId && m.OwnerId == actorId
                && m.Type == MissionType.Quest && NonTerminal.Contains(m.Status), ct);
            if (openByPoster >= settings.MaxOpenQuestsPerPoster)
            {
                return CommandResult.Error($"You already have {settings.MaxOpenQuestsPerPoster} active quest(s) — settle or cancel one before posting another.");
            }
        }

        if (kind == QuestKind.Guild)
        {
            if (!await auth.IsQuestManagerAsync(guildId, actorId, ct))
            {
                return CommandResult.Error("You need to be a quest manager to post a guild quest. Choose a personal quest to fund one from your own balance.");
            }

            // Tier-based bonus points and multiple slots are guild privileges; personal quests are single-taker
            // and tiered by an approver at intake.
            return await quests.PostGuildQuestAsync(guildId, actorId, name, description, currency, reward, deadline, startsAt, tier, repeatable, capacity, ct);
        }

        return await bounties.PostAsync(guildId, actorId, name, currency, reward, description, deadline, startsAt, requestFinalApproval, ct);
    }

    /// <summary>Edit a quest's basic fields (patch semantics: blank/null keeps the current value). Only before
    /// anyone is actively working on it. Reward/tier/capacity are guild-quest-only.</summary>
    public async Task<CommandResult> EditAsync(
        ulong guildId, string idRaw, ulong actorId, string? name = null, string? description = null,
        long? reward = null, DateTimeOffset? deadline = null, QuestTier? tier = null, int? capacity = null, CancellationToken ct = default)
    {
        if (!Guid.TryParse(idRaw, out var id))
        {
            return CommandResult.Error("That doesn't look like a valid quest id.");
        }

        var mission = await db.Missions.Include(m => m.Participants).FirstOrDefaultAsync(m => m.Id == id && m.GuildId == guildId, ct);
        if (mission is null)
        {
            return CommandResult.Error("Quest not found.");
        }

        var isGuild = mission.Origin == MissionOrigin.Guild;
        var canEdit = isGuild ? await auth.IsQuestManagerAsync(guildId, actorId, ct) : mission.OwnerId == actorId;
        if (!canEdit)
        {
            return CommandResult.Error("You can't edit this quest.");
        }

        if (mission.Status is not (MissionStatus.Open or MissionStatus.Scheduled or MissionStatus.PendingApproval))
        {
            return CommandResult.Error("This quest can't be edited in its current state.");
        }

        if (mission.Participants.Any(p => p.Status is MissionParticipantStatus.Claimed or MissionParticipantStatus.Submitted
            or MissionParticipantStatus.RevisionRequested or MissionParticipantStatus.Approved))
        {
            return CommandResult.Error("This quest already has someone working on it — edits are locked after the first claim.");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            mission.Name = name.Trim();
        }

        if (description is not null)
        {
            mission.Description = description.Trim();
        }

        if (deadline.HasValue)
        {
            mission.Deadline = deadline;
        }

        // Reward, tier, and capacity are guild-quest-only edits (personal reward is escrowed — cancel and repost to change it).
        if (isGuild)
        {
            if (reward is { } r)
            {
                if (r <= 0)
                {
                    return CommandResult.Error("Reward must be greater than zero.");
                }

                mission.RewardAmount = r;
            }

            if (tier is { } t)
            {
                mission.Tier = t;
                mission.BonusPoints = (await SettingsAsync(guildId, ct)).PointsForTier(t);
            }

            if (capacity is { } c)
            {
                mission.Capacity = Math.Max(1, c);
            }
        }

        await db.SaveChangesAsync(ct);
        return CommandResult.Ok($"Updated **{mission.Name}**.");
    }

    private static readonly MissionStatus[] NonTerminal =
    [
        MissionStatus.Open, MissionStatus.Scheduled, MissionStatus.PendingApproval,
        MissionStatus.PendingFinal, MissionStatus.Disputed,
    ];

    private async Task<Muster.Domain.Entities.GuildSettings> SettingsAsync(ulong guildId, CancellationToken ct)
        => (await db.Guilds.AsNoTracking().FirstOrDefaultAsync(g => g.Id == guildId, ct))?.Settings ?? new();

    /// <summary>Post a quest, parsing start/expiry date strings the user typed in their own time zone (used by the bot).</summary>
    public async Task<CommandResult> PostParsedAsync(
        ulong guildId, ulong actorId, QuestKind kind, string name, string currency, long reward,
        string description = "", string? startRaw = null, string? expiresRaw = null,
        QuestTier tier = QuestTier.None, bool requestFinalApproval = false, bool repeatable = false, int capacity = 1, CancellationToken ct = default)
    {
        var (startOk, startsAt, startErr) = await timeZones.ParseLocalAsync(guildId, actorId, startRaw, ct);
        if (!startOk)
        {
            return CommandResult.Error(startErr!);
        }

        var (expOk, deadline, expErr) = await timeZones.ParseLocalAsync(guildId, actorId, expiresRaw, ct);
        if (!expOk)
        {
            return CommandResult.Error(expErr!);
        }

        return await PostAsync(guildId, actorId, kind, name, currency, reward, description, startsAt, deadline, tier, requestFinalApproval, repeatable, capacity, ct);
    }

    public async Task<CommandResult> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var board = await db.Missions
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Type == MissionType.Quest
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Scheduled || m.Status == MissionStatus.Disputed
                    || m.Status == MissionStatus.PendingApproval || m.Status == MissionStatus.PendingFinal))
            .OrderBy(m => m.ScheduledStart ?? m.CreatedAt)
            .ToListAsync(ct);

        if (board.Count == 0)
        {
            return CommandResult.Ok("No open quests right now. Post one with `/quest-post`.");
        }

        var codes = await db.Currencies.Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        var lines = board.Select(m =>
        {
            var code = codes.GetValueOrDefault(m.RewardCurrencyId, "?");
            var type = m.Origin == MissionOrigin.Guild ? "Guild" : "Personal";
            var state = m.Status switch
            {
                MissionStatus.PendingApproval => "pending approval",
                MissionStatus.PendingFinal => "awaiting final sign-off",
                MissionStatus.Scheduled when m.ScheduledStart is { } s => $"opens {Rel(s)}",
                MissionStatus.Disputed => "disputed",
                _ when m.Participants.Any(p => p.Status is MissionParticipantStatus.Claimed or MissionParticipantStatus.Submitted) => "taken",
                _ => "open",
            };
            var until = m.Deadline is { } d ? $" · closes {Rel(d)}" : "";
            return $"• **{m.Name}** _[{type}]_ — {m.RewardAmount} {code} ({state}){until}";
        });

        return CommandResult.Ok(
            "**Quest board**\nClaim one with `/quest-claim` and pick it from the list.\n" + string.Join("\n", lines));
    }

    public async Task<CommandResult> ClaimAsync(ulong guildId, string idRaw, ulong userId, CancellationToken ct = default)
    {
        var settings = await SettingsAsync(guildId, ct);
        if (settings.MaxActiveClaimsPerUser > 0)
        {
            var active = await db.MissionParticipants.CountAsync(p => p.Mission!.GuildId == guildId && p.UserId == userId
                && (p.Status == MissionParticipantStatus.Claimed || p.Status == MissionParticipantStatus.Submitted
                    || p.Status == MissionParticipantStatus.RevisionRequested), ct);
            if (active >= settings.MaxActiveClaimsPerUser)
            {
                return CommandResult.Error($"You're already working on {settings.MaxActiveClaimsPerUser} quest(s) — finish or drop one first.");
            }
        }

        return await RouteAsync(idRaw,
            id => quests.ClaimAsync(guildId, id, userId, ct),
            id => bounties.TakeAsync(guildId, id, userId, ct));
    }

    public Task<CommandResult> SubmitAsync(ulong guildId, string idRaw, ulong userId, string? note = null, CancellationToken ct = default)
        => RouteAsync(idRaw,
            id => quests.SubmitAsync(guildId, id, userId, note, ct),
            id => bounties.SubmitAsync(guildId, id, userId, note, ct));

    /// <summary>Reviewer sends a submitted quest back to the worker to revise (manager for guild, owner for personal).</summary>
    public Task<CommandResult> RequestRevisionAsync(ulong guildId, string idRaw, ulong reviewerId, ulong? memberId = null, string? note = null, CancellationToken ct = default)
        => RouteAsync(idRaw,
            async id => !await auth.IsQuestManagerAsync(guildId, reviewerId, ct)
                ? CommandResult.Error("You need to be a quest manager to send a guild-quest submission back.")
                : memberId is { } mid
                    ? await quests.RequestRevisionAsync(guildId, id, mid, reviewerId, note, ct)
                    : CommandResult.Error("Pick which member's submission to send back."),
            id => bounties.RequestRevisionAsync(guildId, id, reviewerId, note, ct));

    /// <summary>Approve a guild quest submission (quest manager). Personal quests are settled with confirm instead.</summary>
    public Task<CommandResult> ApproveAsync(ulong guildId, string idRaw, ulong memberId, ulong reviewerId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            id => quests.ApproveAsync(guildId, id, memberId, reviewerId, ct),
            _ => Task.FromResult(CommandResult.Error("That's a personal quest — its owner settles it with `/quest-confirm`.")));

    /// <summary>Reject a guild quest submission (quest manager).</summary>
    public Task<CommandResult> RejectAsync(ulong guildId, string idRaw, ulong memberId, ulong reviewerId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            id => quests.RejectAsync(guildId, id, memberId, reviewerId, ct),
            _ => Task.FromResult(CommandResult.Error("That's a personal quest — its owner settles it with `/quest-confirm` or `/quest-dispute`.")));

    /// <summary>Confirm a personal quest (owner pays the completer). Guild quests are settled with approve instead.</summary>
    public Task<CommandResult> ConfirmAsync(ulong guildId, string idRaw, ulong ownerId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            _ => Task.FromResult(CommandResult.Error("That's a guild quest — a quest manager settles it with `/quest-approve`.")),
            id => bounties.ConfirmAsync(guildId, id, ownerId, ct));

    public Task<CommandResult> CancelAsync(ulong guildId, string idRaw, ulong actorId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            id => CancelGuildAsync(guildId, id, actorId, ct),
            id => bounties.CancelAsync(guildId, id, actorId, ct));

    public Task<CommandResult> DisputeAsync(ulong guildId, string idRaw, ulong userId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            _ => Task.FromResult(CommandResult.Error("Guild quests can't be disputed — ask a quest manager to review the submission.")),
            id => bounties.DisputeAsync(guildId, id, userId, ct));

    /// <summary>Quest manager accepts a pending personal quest at intake, assigning a difficulty tier.</summary>
    public Task<CommandResult> AcceptIntakeAsync(ulong guildId, string idRaw, QuestTier tier, bool requireFinalApproval, ulong reviewerId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            _ => Task.FromResult(CommandResult.Error("Guild quests are tiered at creation — approve the submission instead.")),
            async id => await auth.IsQuestManagerAsync(guildId, reviewerId, ct)
                ? await bounties.AcceptAsync(guildId, id, tier, requireFinalApproval, reviewerId, ct)
                : CommandResult.Error("You need to be a quest manager to approve a personal quest."));

    /// <summary>Quest manager rejects a pending personal quest at intake → refund the owner.</summary>
    public Task<CommandResult> RejectIntakeAsync(ulong guildId, string idRaw, ulong reviewerId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            _ => Task.FromResult(CommandResult.Error("Guild quests don't go through intake approval.")),
            async id => await auth.IsQuestManagerAsync(guildId, reviewerId, ct)
                ? await bounties.RejectIntakeAsync(guildId, id, reviewerId, ct)
                : CommandResult.Error("You need to be a quest manager to reject a personal quest."));

    /// <summary>Quest manager finalizes a personal quest awaiting sign-off: pay the completer or refund the owner.</summary>
    public Task<CommandResult> FinalizeAsync(ulong guildId, string idRaw, bool pay, ulong reviewerId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            _ => Task.FromResult(CommandResult.Error("Guild quests are settled with approve, not final sign-off.")),
            async id => await auth.IsQuestManagerAsync(guildId, reviewerId, ct)
                ? await bounties.FinalizeAsync(guildId, id, pay, reviewerId, ct)
                : CommandResult.Error("You need to be a quest manager to finalize a personal quest."));

    public Task<CommandResult> ArbitrateAsync(ulong guildId, string idRaw, bool pay, CancellationToken ct = default)
        => bounties.ArbitrateAsync(guildId, idRaw, pay, ct);

    public async Task<CommandResult> SetTimeZoneAsync(ulong userId, string zoneId, CancellationToken ct = default)
    {
        var (ok, error) = await timeZones.SetUserZoneAsync(userId, zoneId, ct);
        return ok
            ? CommandResult.Ok($"Your time zone is now **{zoneId.Trim()}** — quest dates you enter will be read in that zone.")
            : CommandResult.Error(error!);
    }

    private async Task<CommandResult> CancelGuildAsync(ulong guildId, string idRaw, ulong actorId, CancellationToken ct)
    {
        if (!await auth.IsQuestManagerAsync(guildId, actorId, ct))
        {
            return CommandResult.Error("You need to be a quest manager to cancel a guild quest.");
        }

        if (!Guid.TryParse(idRaw, out var id))
        {
            return CommandResult.Error("That doesn't look like a valid quest id.");
        }

        try
        {
            await missions.CancelQuestAsync(id, actorId, ct);
            return CommandResult.Ok("Guild quest cancelled.");
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.Error(ex.Message);
        }
    }

    private async Task<CommandResult> RouteAsync(
        string idRaw, Func<string, Task<CommandResult>> guildAction, Func<string, Task<CommandResult>> playerAction)
    {
        if (!Guid.TryParse(idRaw, out var id))
        {
            return CommandResult.Error("That doesn't look like a valid quest id.");
        }

        var origin = await db.Missions.Where(m => m.Id == id).Select(m => (MissionOrigin?)m.Origin).FirstOrDefaultAsync();
        if (origin is null)
        {
            return CommandResult.Error("Quest not found.");
        }

        return origin == MissionOrigin.Guild ? await guildAction(idRaw) : await playerAction(idRaw);
    }

    private static string Rel(DateTimeOffset time) => $"<t:{time.ToUnixTimeSeconds()}:R>";
}
