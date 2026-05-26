using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Persistence.Queries;

/// <summary>Read queries over the ledger (balances and leaderboards) plus the write-path's own lookups.</summary>
public static class LedgerQueries
{
    /// <summary>The existing ledger entry for an idempotency source key, if any.</summary>
    public static Task<LedgerEntry?> FindLedgerBySourceAsync(
        this MusterDbContext db, LedgerSourceType sourceType, string sourceId, CancellationToken ct = default)
        => db.LedgerEntries.FirstOrDefaultAsync(e => e.SourceType == sourceType && e.SourceId == sourceId, ct);

    /// <summary>A user's wallet for a currency/season scope, if it exists.</summary>
    public static Task<Wallet?> FindWalletAsync(
        this MusterDbContext db, ulong guildId, ulong userId, Guid currencyId, Guid? seasonId, CancellationToken ct = default)
        => db.Wallets.FirstOrDefaultAsync(
            w => w.GuildId == guildId && w.UserId == userId && w.CurrencyId == currencyId && w.SeasonId == seasonId, ct);

    /// <summary>Sum of a user's ledger entries for a currency in a given season scope (null = non-seasonal).</summary>
    public static async Task<long> BalanceAsync(
        this MusterDbContext db, ulong guildId, ulong userId, Guid currencyId, Guid? seasonId, CancellationToken ct = default)
        => await db.LedgerEntries
            .Where(e => e.GuildId == guildId && e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == seasonId)
            .SumAsync(e => (long?)e.Amount, ct) ?? 0;

    /// <summary>A user's most recent ledger entries (newest first), projected for display.</summary>
    public static async Task<List<(Guid CurrencyId, long Amount, LedgerSourceType SourceType, DateTimeOffset OccurredAt, string Reason)>> RecentHistoryAsync(
        this MusterDbContext db, ulong guildId, ulong userId, int count, CancellationToken ct = default)
    {
        var rows = await db.LedgerEntries
            .Where(e => e.GuildId == guildId && e.UserId == userId)
            .OrderByDescending(e => e.Id)
            .Take(count)
            .Select(e => new { e.CurrencyId, e.Amount, e.SourceType, e.OccurredAt, e.Reason })
            .ToListAsync(ct);

        return rows.Select(e => (e.CurrencyId, e.Amount, e.SourceType, e.OccurredAt, e.Reason)).ToList();
    }

    /// <summary>Top members by summed ledger amount for a currency/season scope.</summary>
    public static async Task<List<(ulong UserId, long Total)>> TopByCurrencyAsync(
        this MusterDbContext db, ulong guildId, Guid currencyId, Guid? seasonId, int top, CancellationToken ct = default)
    {
        // Project the grouped aggregate to an anonymous type (translatable on SQL Server), then map in memory.
        var rows = await db.LedgerEntries
            .Where(e => e.GuildId == guildId && e.CurrencyId == currencyId && e.SeasonId == seasonId)
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.Amount) })
            .OrderByDescending(x => x.Total)
            .Take(top)
            .ToListAsync(ct);

        return rows.Select(r => (r.UserId, r.Total)).ToList();
    }
}
