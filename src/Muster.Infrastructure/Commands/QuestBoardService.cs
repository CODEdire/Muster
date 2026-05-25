using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services;

namespace Muster.Infrastructure.Commands;

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
        string description = "", DateTimeOffset? startsAt = null, DateTimeOffset? deadline = null, CancellationToken ct = default)
    {
        if (deadline is { } dl && startsAt is { } st && dl <= st)
        {
            return CommandResult.Error("The expiry must be after the start time.");
        }

        if (kind == QuestKind.Guild)
        {
            if (!await auth.IsQuestManagerAsync(guildId, actorId, ct))
            {
                return CommandResult.Error("You need to be a quest manager to post a guild quest. Choose a personal quest to fund one from your own balance.");
            }

            return await quests.PostGuildQuestAsync(guildId, actorId, name, description, currency, reward, deadline, startsAt, ct);
        }

        return await bounties.PostAsync(guildId, actorId, name, currency, reward, description, deadline, startsAt, ct);
    }

    /// <summary>Post a quest, parsing start/expiry date strings the user typed in their own time zone (used by the bot).</summary>
    public async Task<CommandResult> PostParsedAsync(
        ulong guildId, ulong actorId, QuestKind kind, string name, string currency, long reward,
        string description = "", string? startRaw = null, string? expiresRaw = null, CancellationToken ct = default)
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

        return await PostAsync(guildId, actorId, kind, name, currency, reward, description, startsAt, deadline, ct);
    }

    public async Task<CommandResult> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var board = await db.Missions
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Type == MissionType.Quest
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Scheduled || m.Status == MissionStatus.Disputed))
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

    public Task<CommandResult> ClaimAsync(ulong guildId, string idRaw, ulong userId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            id => quests.ClaimAsync(guildId, id, userId, ct),
            id => bounties.TakeAsync(guildId, id, userId, ct));

    public Task<CommandResult> SubmitAsync(ulong guildId, string idRaw, ulong userId, CancellationToken ct = default)
        => RouteAsync(idRaw,
            id => quests.SubmitAsync(guildId, id, userId, ct),
            id => bounties.SubmitAsync(guildId, id, userId, ct));

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
