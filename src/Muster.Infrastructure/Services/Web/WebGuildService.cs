using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
namespace Muster.Infrastructure.Services.Web;

public record UserGuildView(ulong GuildId, string Name, bool IsAdmin);

/// <summary>The signed-in user's profile for the shell (display name + optional avatar URL).</summary>
public record UserProfile(string Name, string? AvatarUrl);

public record LeaderboardRow(int Rank, ulong UserId, string DisplayName, long Total);

/// <summary>A guild-wide ledger movement for the admin overview feed, with the member named.</summary>
public record MovementRow(ulong UserId, string DisplayName, string CurrencyCode, long Amount, string Source, DateTimeOffset OccurredAt, string Reason);

/// <summary>The admin currency overview for one currency: supply analytics + top holders + a recent-movement feed.</summary>
public record CurrencyOverview(CurrencySupply Supply, IReadOnlyList<LeaderboardRow> TopHolders, IReadOnlyList<MovementRow> Recent);

/// <summary>Read models for the web UI: a user's guilds and a guild's leaderboard with display names.</summary>
public class WebGuildService(MusterDbContext db, GuildAuthorizationService auth, ICurrencyReadService scores)
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

    /// <summary>Leaderboard with member display names resolved. Null/blank <paramref name="code"/> = season POINTS;
    /// otherwise the top holders of that currency (seasonal currencies scope to the active season).</summary>
    public async Task<IReadOnlyList<LeaderboardRow>> GetLeaderboardAsync(
        ulong guildId, string? code = null, int top = 25, CancellationToken ct = default)
    {
        var entries = string.IsNullOrWhiteSpace(code)
            ? await scores.GetSeasonLeaderboardAsync(guildId, top, ct)
            : await scores.GetLeaderboardAsync(guildId, code, top, ct);
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

    /// <summary>The admin overview for one currency (by code): supply analytics, top holders (escrow/house account
    /// excluded), and a recent guild-wide movement feed with member names. Null when the currency is unknown.</summary>
    public async Task<CurrencyOverview?> GetCurrencyOverviewAsync(
        ulong guildId, string code, int topHolders = 10, int feed = 25, CancellationToken ct = default)
    {
        var supply = await scores.GetSupplyAsync(guildId, code, ct);
        if (supply is null)
        {
            return null;
        }

        // Pull a couple extra holders so dropping the house account still leaves a full list.
        var holders = (await scores.GetLeaderboardAsync(guildId, code, topHolders + 2, ct))
            .Where(e => e.UserId != CurrencyService.EscrowAccountUserId)
            .Take(topHolders)
            .ToList();
        var movements = await scores.GetGuildMovementsAsync(guildId, code, skip: 0, take: feed, ct);

        var ids = holders.Select(h => h.UserId).Concat(movements.Select(m => m.UserId)).Distinct().ToList();
        var names = await db.UserDisplayNameMapAsync(ids, ct);
        string Name(ulong id) => id == CurrencyService.EscrowAccountUserId ? "Escrow (house)" : names.GetValueOrDefault(id, id.ToString());

        var topRows = holders
            .Select((e, i) => new LeaderboardRow(i + 1, e.UserId, Name(e.UserId), e.Total))
            .ToList();
        var feedRows = movements
            .Select(m => new MovementRow(m.UserId, Name(m.UserId), m.CurrencyCode, m.Amount, m.SourceType, m.OccurredAt, m.Reason))
            .ToList();

        return new CurrencyOverview(supply, topRows, feedRows);
    }
}
