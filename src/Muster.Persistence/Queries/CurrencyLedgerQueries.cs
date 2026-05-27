using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Persistence.Queries;

/// <summary>A ledger entry projected for the public API (source type flattened to a string).</summary>
public record LedgerEntryView(
    long Id, ulong UserId, Guid CurrencyId, Guid? SeasonId, long Amount, string SourceType, DateTimeOffset OccurredAt, string Reason);

/// <summary>A summed balance for one (user, currency, season) scope — the ledger truth used to rebuild the cache.</summary>
public record LedgerTotal(ulong UserId, Guid CurrencyId, Guid? SeasonId, long Total);

/// <summary>Supply analytics for one currency/season scope, summed from the authoritative ledger.
/// <see cref="Circulating"/> is member-held (escrow excluded); <see cref="Escrow"/> is held by the house account;
/// <see cref="GrossCredited"/>/<see cref="GrossDebited"/> are all-time inflow/outflow (both non-negative).</summary>
public record CurrencySupplyTotals(long GrossCredited, long GrossDebited, long Circulating, long Escrow, int Holders);

/// <summary>Read queries over the ledger (balances and leaderboards) plus the write-path's own lookups.</summary>
public static class CurrencyLedgerQueries
{
    /// <summary>Every (user, currency, season) balance in a guild, summed from the ledger — the source of truth the
    /// wallet-cache rebuild reconciles against.</summary>
    public static async Task<List<LedgerTotal>> LedgerTotalsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
    {
        var rows = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId)
            .GroupBy(e => new { e.UserId, e.CurrencyId, e.SeasonId })
            .Select(g => new { g.Key.UserId, g.Key.CurrencyId, g.Key.SeasonId, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        return rows.Select(r => new LedgerTotal(r.UserId, r.CurrencyId, r.SeasonId, r.Total)).ToList();
    }
    /// <summary>A guild's ledger entries, newest first, paged (take clamped 1..100) — the public API's ledger feed.</summary>
    public static async Task<List<LedgerEntryView>> PagedLedgerAsync(
        this MusterDbContext db, ulong guildId, int skip, int take, CancellationToken ct = default)
    {
        var rows = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId)
            .OrderByDescending(e => e.Id)
            .Skip(Math.Max(skip, 0))
            .Take(Math.Clamp(take, 1, 100))
            .Select(e => new { e.Id, e.UserId, e.CurrencyId, e.SeasonId, e.Amount, e.SourceType, e.OccurredAt, e.Reason })
            .ToListAsync(ct);

        return rows.Select(e => new LedgerEntryView(
            e.Id, e.UserId, e.CurrencyId, e.SeasonId, e.Amount, e.SourceType.ToString(), e.OccurredAt, e.Reason)).ToList();
    }

    /// <summary>The existing ledger entry for an idempotency source key, if any.</summary>
    public static Task<CurrencyLedgerEntry?> FindLedgerBySourceAsync(
        this MusterDbContext db, CurrencyLedgerSource sourceType, string sourceId, CancellationToken ct = default)
        => db.CurrencyLedgerEntries.FirstOrDefaultAsync(e => e.SourceType == sourceType && e.SourceId == sourceId, ct);

    /// <summary>A single ledger entry by id (the outbound connector reads it for source type + reason).</summary>
    public static Task<CurrencyLedgerEntry?> FindLedgerByIdAsync(this MusterDbContext db, long id, CancellationToken ct = default)
        => db.CurrencyLedgerEntries.FirstOrDefaultAsync(e => e.Id == id, ct);

    /// <summary>User ids holding a (non-seasonal) wallet for a currency — the set to reconcile against an external system.</summary>
    public static Task<List<ulong>> ListWalletUserIdsAsync(this MusterDbContext db, ulong guildId, Guid currencyId, CancellationToken ct = default)
        => db.Wallets.Where(w => w.GuildId == guildId && w.CurrencyId == currencyId && w.SeasonId == null)
            .Select(w => w.UserId).ToListAsync(ct);

    /// <summary>User ids whose wallet hasn't been synced since the cutoff (or never) — the periodic sweep's work list.</summary>
    public static Task<List<ulong>> ListWalletsNeedingSyncAsync(
        this MusterDbContext db, ulong guildId, Guid currencyId, DateTimeOffset cutoff, CancellationToken ct = default)
        => db.Wallets.Where(w => w.GuildId == guildId && w.CurrencyId == currencyId && w.SeasonId == null
                && (w.LastSyncedAt == null || w.LastSyncedAt < cutoff))
            .Select(w => w.UserId).ToListAsync(ct);

    /// <summary>Every wallet row in a guild (tracked) — the set the wallet-cache rebuild reconciles against the ledger.</summary>
    public static Task<List<Wallet>> WalletsForGuildAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Wallets.Where(w => w.GuildId == guildId).ToListAsync(ct);

    /// <summary>A user's wallet for a currency/season scope, if it exists.</summary>
    public static Task<Wallet?> FindWalletAsync(
        this MusterDbContext db, ulong guildId, ulong userId, Guid currencyId, Guid? seasonId, CancellationToken ct = default)
        => db.Wallets.FirstOrDefaultAsync(
            w => w.GuildId == guildId && w.UserId == userId && w.CurrencyId == currencyId && w.SeasonId == seasonId, ct);

    /// <summary>
    /// Sum of a user's ledger entries for a currency in a given season scope (null = non-seasonal). This is the
    /// **transaction authority** (the source of truth) — overdraft/spend/transfer decisions sum the ledger so a
    /// drifted cache can never enable an overdraft. Cheap *display* reads use the wallet cache instead
    /// (<see cref="WalletBalancesAsync"/> / <see cref="TopWalletBalancesAsync"/>).
    /// </summary>
    public static async Task<long> BalanceAsync(
        this MusterDbContext db, ulong guildId, ulong userId, Guid currencyId, Guid? seasonId, CancellationToken ct = default)
        => await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == seasonId)
            .SumAsync(e => (long?)e.Amount, ct) ?? 0;

    /// <summary>A user's cached wallet balances keyed by (currencyId, seasonId) — cheap display reads (dashboard,
    /// balance commands). The cache is kept in lock-step with the ledger and rebuildable; not the txn authority.</summary>
    public static async Task<Dictionary<(Guid CurrencyId, Guid? SeasonId), long>> WalletBalancesAsync(
        this MusterDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var rows = await db.Wallets
            .Where(w => w.GuildId == guildId && w.UserId == userId)
            .Select(w => new { w.CurrencyId, w.SeasonId, w.Balance })
            .ToListAsync(ct);

        return rows.ToDictionary(w => (w.CurrencyId, w.SeasonId), w => w.Balance);
    }

    /// <summary>Every member's cached balance for one currency/season scope, keyed by user id — the admin roster's
    /// balance column (members with no wallet row simply won't appear, so the caller defaults them to zero).</summary>
    public static async Task<Dictionary<ulong, long>> WalletColumnAsync(
        this MusterDbContext db, ulong guildId, Guid currencyId, Guid? seasonId, CancellationToken ct = default)
    {
        var rows = await db.Wallets
            .Where(w => w.GuildId == guildId && w.CurrencyId == currencyId && w.SeasonId == seasonId)
            .Select(w => new { w.UserId, w.Balance })
            .ToListAsync(ct);

        return rows.ToDictionary(w => w.UserId, w => w.Balance);
    }

    /// <summary>Top members by cached wallet balance for a currency/season scope — the leaderboard's display read.</summary>
    public static async Task<List<(ulong UserId, long Total)>> TopWalletBalancesAsync(
        this MusterDbContext db, ulong guildId, Guid currencyId, Guid? seasonId, int top, CancellationToken ct = default)
    {
        var rows = await db.Wallets
            .Where(w => w.GuildId == guildId && w.CurrencyId == currencyId && w.SeasonId == seasonId && w.Balance != 0)
            .OrderByDescending(w => w.Balance)
            .Take(top)
            .Select(w => new { w.UserId, w.Balance })
            .ToListAsync(ct);

        return rows.Select(w => (w.UserId, w.Balance)).ToList();
    }

    /// <summary>A member's ledger entries (newest first), optionally filtered to one currency, paged (take clamped
    /// 1..100). The per-member history feed behind the Discord <c>/currency history</c>, the web wallet, and the API.</summary>
    public static async Task<List<(Guid CurrencyId, long Amount, CurrencyLedgerSource SourceType, DateTimeOffset OccurredAt, string Reason)>> MemberLedgerAsync(
        this MusterDbContext db, ulong guildId, ulong userId, Guid? currencyId, int skip, int take, CancellationToken ct = default)
    {
        var rows = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && e.UserId == userId && (currencyId == null || e.CurrencyId == currencyId))
            .OrderByDescending(e => e.Id)
            .Skip(Math.Max(skip, 0))
            .Take(Math.Clamp(take, 1, 100))
            .Select(e => new { e.CurrencyId, e.Amount, e.SourceType, e.OccurredAt, e.Reason })
            .ToListAsync(ct);

        return rows.Select(e => (e.CurrencyId, e.Amount, e.SourceType, e.OccurredAt, e.Reason)).ToList();
    }

    /// <summary>A user's most recent ledger entries across all currencies (newest first), projected for display.</summary>
    public static Task<List<(Guid CurrencyId, long Amount, CurrencyLedgerSource SourceType, DateTimeOffset OccurredAt, string Reason)>> RecentHistoryAsync(
        this MusterDbContext db, ulong guildId, ulong userId, int count, CancellationToken ct = default)
        => db.MemberLedgerAsync(guildId, userId, currencyId: null, skip: 0, take: count, ct);

    /// <summary>A guild's ledger entries for a currency (or all currencies when null), newest first, paged (take
    /// clamped 1..100) — the admin currency-overview movement feed. Includes who (UserId) so the feed can name them.</summary>
    public static async Task<List<(ulong UserId, Guid CurrencyId, long Amount, CurrencyLedgerSource SourceType, DateTimeOffset OccurredAt, string Reason)>> GuildLedgerAsync(
        this MusterDbContext db, ulong guildId, Guid? currencyId, int skip, int take, CancellationToken ct = default)
    {
        var rows = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && (currencyId == null || e.CurrencyId == currencyId))
            .OrderByDescending(e => e.Id)
            .Skip(Math.Max(skip, 0))
            .Take(Math.Clamp(take, 1, 100))
            .Select(e => new { e.UserId, e.CurrencyId, e.Amount, e.SourceType, e.OccurredAt, e.Reason })
            .ToListAsync(ct);

        return rows.Select(e => (e.UserId, e.CurrencyId, e.Amount, e.SourceType, e.OccurredAt, e.Reason)).ToList();
    }

    /// <summary>Supply analytics for a currency/season scope, summed from the ledger (the authority). Member-held
    /// circulation is net-of-escrow; <paramref name="escrowUserId"/> is the house account whose holdings are split out.</summary>
    public static async Task<CurrencySupplyTotals> CurrencySupplyAsync(
        this MusterDbContext db, ulong guildId, Guid currencyId, Guid? seasonId, ulong escrowUserId, CancellationToken ct = default)
    {
        var agg = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && e.CurrencyId == currencyId && e.SeasonId == seasonId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                GrossCredited = g.Sum(x => x.Amount > 0 ? x.Amount : 0L),
                GrossDebited = g.Sum(x => x.Amount < 0 ? -x.Amount : 0L),
                Net = g.Sum(x => x.Amount),
                Escrow = g.Sum(x => x.UserId == escrowUserId ? x.Amount : 0L),
            })
            .FirstOrDefaultAsync(ct);

        if (agg is null)
        {
            return new CurrencySupplyTotals(0, 0, 0, 0, 0);
        }

        var holders = await db.Wallets
            .Where(w => w.GuildId == guildId && w.CurrencyId == currencyId && w.SeasonId == seasonId
                && w.UserId != escrowUserId && w.Balance > 0)
            .CountAsync(ct);

        return new CurrencySupplyTotals(agg.GrossCredited, agg.GrossDebited, agg.Net - agg.Escrow, agg.Escrow, holders);
    }

    /// <summary>Top members by summed ledger amount for a currency/season scope.</summary>
    public static async Task<List<(ulong UserId, long Total)>> TopByCurrencyAsync(
        this MusterDbContext db, ulong guildId, Guid currencyId, Guid? seasonId, int top, CancellationToken ct = default)
    {
        // Project the grouped aggregate to an anonymous type (translatable on SQL Server), then map in memory.
        var rows = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && e.CurrencyId == currencyId && e.SeasonId == seasonId)
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.Amount) })
            .OrderByDescending(x => x.Total)
            .Take(top)
            .ToListAsync(ct);

        return rows.Select(r => (r.UserId, r.Total)).ToList();
    }
}
