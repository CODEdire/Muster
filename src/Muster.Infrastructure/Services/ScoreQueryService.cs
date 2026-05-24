using Microsoft.EntityFrameworkCore;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

public record LeaderboardEntry(ulong UserId, long Total);

public record WalletBalance(string CurrencyCode, string CurrencyName, long Balance, bool IsSeasonal);

/// <summary>Read-side queries over the ledger for scores, leaderboards, and wallets.</summary>
public class ScoreQueryService(MusterDbContext db)
{
    /// <summary>Top members by POINTS in the guild's active season, computed from the ledger.</summary>
    public async Task<IReadOnlyList<LeaderboardEntry>> GetSeasonLeaderboardAsync(
        ulong guildId, int top = 25, CancellationToken ct = default)
    {
        var season = await db.Seasons
            .FirstOrDefaultAsync(s => s.GuildId == guildId && s.Status == SeasonStatus.Active, ct);
        var points = await db.Currencies
            .FirstOrDefaultAsync(c => c.GuildId == guildId && c.Code == GuildProvisioningService.PointsCurrencyCode, ct);

        if (season is null || points is null)
        {
            return [];
        }

        return await db.LedgerEntries
            .Where(e => e.GuildId == guildId && e.CurrencyId == points.Id && e.SeasonId == season.Id)
            .GroupBy(e => e.UserId)
            .Select(g => new LeaderboardEntry(g.Key, g.Sum(x => x.Amount)))
            .OrderByDescending(x => x.Total)
            .Take(top)
            .ToListAsync(ct);
    }

    /// <summary>A member's balances across every currency in the guild (seasonal points use the active season).</summary>
    public async Task<IReadOnlyList<WalletBalance>> GetWalletsAsync(
        ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var activeSeasonId = await db.Seasons
            .Where(s => s.GuildId == guildId && s.Status == SeasonStatus.Active)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        var currencies = await db.Currencies
            .Where(c => c.GuildId == guildId)
            .ToListAsync(ct);

        var balances = new List<WalletBalance>(currencies.Count);
        foreach (var currency in currencies)
        {
            Guid? seasonFilter = currency.IsSeasonal ? activeSeasonId : null;

            var balance = await db.LedgerEntries
                .Where(e => e.GuildId == guildId
                    && e.UserId == userId
                    && e.CurrencyId == currency.Id
                    && e.SeasonId == seasonFilter)
                .SumAsync(e => (long?)e.Amount, ct) ?? 0;

            balances.Add(new WalletBalance(currency.Code, currency.Name, balance, currency.IsSeasonal));
        }

        return balances;
    }
}
