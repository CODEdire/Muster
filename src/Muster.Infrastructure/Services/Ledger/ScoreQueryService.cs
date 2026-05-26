using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Ledger;

public record LeaderboardEntry(ulong UserId, long Total);

public record WalletBalance(string CurrencyCode, string CurrencyName, long Balance, bool IsSeasonal);

/// <summary>Read-side queries over the ledger for scores, leaderboards, and wallets.</summary>
public class ScoreQueryService(MusterDbContext db)
{
    /// <summary>Top members by POINTS in the guild's active season, computed from the ledger.</summary>
    public async Task<IReadOnlyList<LeaderboardEntry>> GetSeasonLeaderboardAsync(
        ulong guildId, int top = 25, CancellationToken ct = default)
    {
        var season = await db.FindActiveSeasonAsync(guildId, ct);
        var points = await db.FindPointsAsync(guildId, ct);

        if (season is null || points is null)
        {
            return [];
        }

        var rows = await db.TopByCurrencyAsync(guildId, points.Id, season.Id, top, ct);
        return rows.Select(r => new LeaderboardEntry(r.UserId, r.Total)).ToList();
    }

    /// <summary>A member's balances across every currency in the guild (seasonal points use the active season).</summary>
    public async Task<IReadOnlyList<WalletBalance>> GetWalletsAsync(
        ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var activeSeasonId = await db.ActiveSeasonIdAsync(guildId, ct);
        var currencies = await db.ListCurrenciesAsync(guildId, ct);

        var balances = new List<WalletBalance>(currencies.Count);
        foreach (var currency in currencies)
        {
            Guid? seasonFilter = currency.IsSeasonal ? activeSeasonId : null;
            var balance = await db.BalanceAsync(guildId, userId, currency.Id, seasonFilter, ct);
            balances.Add(new WalletBalance(currency.Code, currency.Name, balance, currency.IsSeasonal));
        }

        return balances;
    }
}
