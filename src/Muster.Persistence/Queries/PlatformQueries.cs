using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Persistence.Queries;

/// <summary>A posted quest-board card whose quest has reached a terminal state (candidate for cleanup).</summary>
public record QuestBoardCard(Guid PostedMessageId, ulong GuildId, ulong ChannelId, ulong MessageId, DateTimeOffset StatusChangedAt);

/// <summary>Queries over platform entities (API clients).</summary>
public static class PlatformQueries
{
    /// <summary>An active API client matching a key hash, for authentication.</summary>
    public static Task<ApiClient?> FindActiveApiClientByHashAsync(this MusterDbContext db, string hash, CancellationToken ct = default)
        => db.ApiClients.FirstOrDefaultAsync(c => c.IsActive && c.ApiKeyHash == hash, ct);

    /// <summary>An API client within a guild.</summary>
    public static Task<ApiClient?> FindApiClientAsync(this MusterDbContext db, ulong guildId, Guid clientId, CancellationToken ct = default)
        => db.ApiClients.FirstOrDefaultAsync(c => c.Id == clientId && c.GuildId == guildId, ct);

    /// <summary>A guild's API clients, by name.</summary>
    public static Task<List<ApiClient>> ListApiClientsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.ApiClients.Where(c => c.GuildId == guildId).OrderBy(c => c.Name).ToListAsync(ct);

    /// <summary>The tracked board-message link for an entity (e.g. a quest), or null if none posted yet.</summary>
    public static Task<PostedMessage?> FindPostedMessageAsync(this MusterDbContext db, string entityType, Guid entityId, CancellationToken ct = default)
        => db.PostedMessages.FirstOrDefaultAsync(m => m.EntityType == entityType && m.EntityId == entityId, ct);

    /// <summary>Quest-board cards whose quest is terminal (Closed/Cancelled/Expired) — the bot prunes the stale ones
    /// (older than the guild's retention) so completed quests don't linger forever. ("quest" = the board EntityType.)</summary>
    public static Task<List<QuestBoardCard>> ListTerminalQuestBoardCardsAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.PostedMessages
            .Where(p => p.EntityType == "quest")
            .Join(db.Quests, p => p.EntityId, q => q.Id, (p, q) => new { p, q.Status, q.StatusChangedAt })
            .Where(x => x.Status == QuestStatus.Closed || x.Status == QuestStatus.Cancelled || x.Status == QuestStatus.Expired)
            .Select(x => new QuestBoardCard(x.p.Id, x.p.GuildId, x.p.ChannelId, x.p.MessageId, x.StatusChangedAt))
            .ToListAsync(ct);
}
