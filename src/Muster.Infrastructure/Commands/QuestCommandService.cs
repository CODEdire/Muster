using Muster.Domain.Entities;
using Muster.Infrastructure.Services;

namespace Muster.Infrastructure.Commands;

/// <summary>Platform-independent logic for the quest board commands.</summary>
public class QuestCommandService(MissionService missions)
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

    /// <summary>Create a guild quest minting a chosen currency (no balance required — the guild issues it).</summary>
    public async Task<CommandResult> PostGuildQuestAsync(
        ulong guildId, ulong actorId, string name, string description, string currencyCode, long reward,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide a quest name.");
        }

        if (reward <= 0)
        {
            return CommandResult.Error("Reward must be greater than zero.");
        }

        var quest = await missions.CreateGuildQuestAsync(guildId, name.Trim(), (description ?? string.Empty).Trim(), actorId, currencyCode, reward, ct);
        return quest is null
            ? CommandResult.Error($"Unknown currency '{currencyCode}'.")
            : CommandResult.Ok($"Guild quest **{quest.Name}** posted (`{quest.Id}`) — reward **{reward} {currencyCode.Trim().ToUpperInvariant()}** (minted on approval).");
    }

    public async Task<CommandResult> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var quests = await missions.ListOpenQuestsAsync(guildId, ct);
        return CommandResult.Ok(FormatQuests(quests));
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

    public static string FormatQuests(IReadOnlyList<Mission> quests)
    {
        if (quests.Count == 0)
        {
            return "No open quests right now.";
        }

        var lines = quests.Select(q => $"`{q.Id}` — **{q.Name}**: {q.Description} (reward {q.RewardAmount})");
        return "**Open quests**\n" + string.Join("\n", lines);
    }

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
