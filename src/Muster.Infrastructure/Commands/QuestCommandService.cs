using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services;

namespace Muster.Infrastructure.Commands;

/// <summary>Platform-independent logic for the quest board commands.</summary>
public class QuestCommandService(MissionService missions, MusterDbContext db)
{
    public async Task<CommandResult> PostAsync(
        ulong guildId, ulong actorId, string name, string description, long reward, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide a quest name.");
        }

        if (reward < 0)
        {
            return CommandResult.Error("Reward can't be negative.");
        }

        var quest = await missions.CreateQuestPointsAsync(guildId, name.Trim(), (description ?? string.Empty).Trim(), actorId, reward, ct: ct);
        return CommandResult.Ok($"Quest **{quest.Name}** posted (`{quest.Id}`) — reward **{reward}** points.");
    }

    /// <summary>Create a guild quest minting a chosen spendable currency, with optional tier-based bonus POINTS.</summary>
    public async Task<CommandResult> PostGuildQuestAsync(
        ulong guildId, ulong actorId, string name, string description, string currencyCode, long reward,
        DateTimeOffset? deadline = null, DateTimeOffset? startsAt = null, QuestTier tier = QuestTier.None, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide a quest name.");
        }

        if (reward <= 0)
        {
            return CommandResult.Error("Reward must be greater than zero.");
        }

        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.GuildId == guildId && c.Code == code, ct);
        if (currency is null)
        {
            return CommandResult.Error($"Unknown currency '{code}'.");
        }

        if (!currency.IsSpendable)
        {
            return CommandResult.Error($"{code} can't be a quest reward — choose a spendable currency (e.g. COIN).");
        }

        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        var bonusPoints = guild?.Settings.PointsForTier(tier) ?? 0;

        var quest = await missions.CreateQuestAsync(
            guildId, name.Trim(), (description ?? string.Empty).Trim(), actorId,
            currency.Id, reward, deadline, startsAt, bonusPoints, tier, ct: ct);

        var when = quest.Status == MissionStatus.Scheduled && startsAt is { } st ? $" — opens {FormatTime(st)}" : "";
        var until = deadline is { } d ? $" — closes {FormatTime(d)}" : "";
        var bonus = bonusPoints > 0 ? $" + **{bonusPoints} POINTS** (tier {tier})" : "";
        return CommandResult.Ok($"Guild quest **{quest.Name}** posted — reward **{reward} {code}**{bonus} (minted on approval){when}{until}.");
    }

    public async Task<CommandResult> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var quests = await missions.ListOpenQuestsAsync(guildId, ct);
        var codes = await db.Currencies.Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        return CommandResult.Ok(FormatQuests(quests, codes));
    }

    public async Task<CommandResult> ClaimAsync(ulong guildId, string questIdRaw, ulong userId, CancellationToken ct = default)
        => await GuardedAsync(questIdRaw, async id =>
        {
            await missions.ClaimAsync(id, userId, ct);
            return CommandResult.Ok("Quest claimed. Use `/quest-submit` when you've completed it.");
        });

    public async Task<CommandResult> SubmitAsync(ulong guildId, string questIdRaw, ulong userId, CancellationToken ct = default)
        => await GuardedAsync(questIdRaw, async id =>
        {
            await missions.SubmitAsync(id, userId, ct);
            return CommandResult.Ok("Quest submitted for approval.");
        });

    public async Task<CommandResult> ApproveAsync(
        ulong guildId, string questIdRaw, ulong memberId, ulong reviewerId, CancellationToken ct = default)
        => await GuardedAsync(questIdRaw, async id =>
        {
            await missions.ApproveAsync(id, memberId, reviewerId, ct);
            return CommandResult.Ok($"Approved <@{memberId}>'s quest and awarded the reward.");
        });

    public async Task<CommandResult> RejectAsync(
        ulong guildId, string questIdRaw, ulong memberId, ulong reviewerId, CancellationToken ct = default)
        => await GuardedAsync(questIdRaw, async id =>
        {
            await missions.RejectAsync(id, memberId, reviewerId, ct);
            return CommandResult.Ok($"Rejected <@{memberId}>'s submission.");
        });

    public static string FormatQuests(IReadOnlyList<Mission> quests, IReadOnlyDictionary<Guid, string> codes)
    {
        if (quests.Count == 0)
        {
            return "No open quests right now.";
        }

        var lines = quests.Select(q =>
        {
            var code = codes.GetValueOrDefault(q.RewardCurrencyId, "");
            var details = string.IsNullOrWhiteSpace(q.Description) ? "" : $" — {q.Description}";
            var until = q.Deadline is { } d ? $" · closes {FormatTime(d)}" : "";
            return $"• **{q.Name}** (reward {q.RewardAmount} {code}){details}{until}";
        });
        return "**Open quests**\nClaim one with `/quest-claim` and pick it from the list.\n" + string.Join("\n", lines);
    }

    internal static string FormatTime(DateTimeOffset time) => $"<t:{time.ToUnixTimeSeconds()}:R>";

    private static async Task<CommandResult> GuardedAsync(string questIdRaw, Func<Guid, Task<CommandResult>> action)
    {
        if (!Guid.TryParse(questIdRaw, out var id))
        {
            return CommandResult.Error("That doesn't look like a valid quest id.");
        }

        try
        {
            return await action(id);
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.Error(ex.Message);
        }
    }
}
