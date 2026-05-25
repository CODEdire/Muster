using Microsoft.EntityFrameworkCore;

using Muster.Infrastructure.Persistence;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
namespace Muster.Infrastructure.Services.Web;

public record UserGuildView(ulong GuildId, string Name, bool IsAdmin);

public record LeaderboardRow(int Rank, ulong UserId, string DisplayName, long Total);

/// <summary>Read models for the web UI: a user's guilds and a guild's leaderboard with display names.</summary>
public class WebGuildService(MusterDbContext db, GuildAuthorizationService auth, ScoreQueryService scores)
{
    /// <summary>Active guilds the user belongs to (or owns), with their admin status in each.</summary>
    public async Task<IReadOnlyList<UserGuildView>> GetGuildsForUserAsync(ulong userId, CancellationToken ct = default)
    {
        var memberGuildIds = await db.GuildMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.GuildId)
            .ToListAsync(ct);

        var guilds = await db.Guilds
            .Where(g => g.IsActive && (g.OwnerId == userId || memberGuildIds.Contains(g.Id)))
            .OrderBy(g => g.Name)
            .ToListAsync(ct);

        var views = new List<UserGuildView>(guilds.Count);
        foreach (var guild in guilds)
        {
            views.Add(new UserGuildView(guild.Id, guild.Name, await auth.IsAdminAsync(guild.Id, userId, ct)));
        }

        return views;
    }

    public async Task<bool> CanViewGuildAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var owns = await db.Guilds.AnyAsync(g => g.Id == guildId && g.OwnerId == userId, ct);
        if (owns)
        {
            return true;
        }

        return await db.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
    }

    /// <summary>Season leaderboard with member display names resolved.</summary>
    public async Task<IReadOnlyList<LeaderboardRow>> GetLeaderboardAsync(ulong guildId, int top = 25, CancellationToken ct = default)
    {
        var entries = await scores.GetSeasonLeaderboardAsync(guildId, top, ct);
        if (entries.Count == 0)
        {
            return [];
        }

        var ids = entries.Select(e => e.UserId).ToList();
        var names = await db.Users
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.GlobalName ?? u.Username, ct);

        return entries
            .Select((e, i) => new LeaderboardRow(i + 1, e.UserId, names.GetValueOrDefault(e.UserId, e.UserId.ToString()), e.Total))
            .ToList();
    }
}
