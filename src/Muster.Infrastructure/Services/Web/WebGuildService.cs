using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
namespace Muster.Infrastructure.Services.Web;

public record UserGuildView(ulong GuildId, string Name, bool IsAdmin);

/// <summary>The signed-in user's profile for the shell (display name + optional avatar URL).</summary>
public record UserProfile(string Name, string? AvatarUrl);

public record LeaderboardRow(int Rank, ulong UserId, string DisplayName, long Total);

/// <summary>Read models for the web UI: a user's guilds and a guild's leaderboard with display names.</summary>
public class WebGuildService(MusterDbContext db, GuildAuthorizationService auth, ScoreQueryService scores)
{
    /// <summary>Active guilds the user belongs to (or owns), with their admin status in each.</summary>
    public async Task<IReadOnlyList<UserGuildView>> GetGuildsForUserAsync(ulong userId, CancellationToken ct = default)
    {
        var memberGuildIds = await db.ListMemberGuildIdsAsync(userId, ct);
        var guilds = await db.ListActiveGuildsForUserAsync(userId, memberGuildIds, ct);

        var views = new List<UserGuildView>(guilds.Count);
        foreach (var guild in guilds)
        {
            views.Add(new UserGuildView(guild.Id, guild.Name, await auth.IsAdminAsync(guild.Id, userId, ct)));
        }

        return views;
    }

    /// <summary>The signed-in user's profile, or null if we haven't synced them yet (caller falls back to claims).</summary>
    public async Task<UserProfile?> GetUserProfileAsync(ulong userId, CancellationToken ct = default)
    {
        var user = await db.FindUserAsync(userId, ct);
        return user is null ? null : new UserProfile(user.GlobalName ?? user.Username, Discord.DiscordCdn.AvatarUrl(user.Id, user.AvatarHash));
    }

    public async Task<bool> CanViewGuildAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        if (await db.IsGuildOwnerAsync(guildId, userId, ct))
        {
            return true;
        }

        return await db.IsMemberAsync(guildId, userId, ct);
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
        var names = await db.UserDisplayNameMapAsync(ids, ct);

        return entries
            .Select((e, i) => new LeaderboardRow(i + 1, e.UserId, names.GetValueOrDefault(e.UserId, e.UserId.ToString()), e.Total))
            .ToList();
    }
}
