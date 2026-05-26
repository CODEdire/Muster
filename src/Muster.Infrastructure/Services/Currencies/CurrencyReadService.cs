using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Currencies;

public record LeaderboardEntry(ulong UserId, long Total);

public record WalletBalance(string CurrencyCode, string CurrencyName, long Balance, bool IsSeasonal);

/// <summary>One ledger entry projected for display (currency resolved to its code; amount signed, source as a string).</summary>
public record LedgerHistoryEntry(string CurrencyCode, long Amount, string SourceType, DateTimeOffset OccurredAt, string Reason);

/// <summary>A currency in the member-facing directory ("what exists / what's it for").</summary>
public record CurrencyInfo(string Code, string Name, bool Spendable, bool Seasonal);

/// <summary>
/// The currency <b>read model</b> (CQRS query side): leaderboards and wallet balances for display. These are
/// display reads, so they serve from the cheap <c>Wallet.Balance</c> cache (kept in lock-step with the ledger,
/// rebuildable) rather than summing the ledger — the ledger SUM stays reserved for transaction-authority
/// decisions (overdraft) in <see cref="CurrencyService"/>. Callers depend on this interface.
/// </summary>
public interface ICurrencyReadService
{
    /// <summary>Top members by POINTS in the guild's active season, from the wallet cache.</summary>
    Task<IReadOnlyList<LeaderboardEntry>> GetSeasonLeaderboardAsync(ulong guildId, int top = 25, CancellationToken ct = default);

    /// <summary>Top members by balance for any currency (by code), from the wallet cache. Seasonal currencies use
    /// the active season; empty when the currency is unknown.</summary>
    Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(ulong guildId, string code, int top = 25, CancellationToken ct = default);

    /// <summary>A member's balances across every currency in the guild (seasonal points use the active season).</summary>
    Task<IReadOnlyList<WalletBalance>> GetWalletsAsync(ulong guildId, ulong userId, CancellationToken ct = default);

    /// <summary>A member's ledger history (newest first), optionally filtered to one currency (by code), paged.
    /// Currency ids are resolved to codes. Empty when a given code is unknown.</summary>
    Task<IReadOnlyList<LedgerHistoryEntry>> GetMemberHistoryAsync(
        ulong guildId, ulong userId, string? code, int skip, int take, CancellationToken ct = default);

    /// <summary>The guild's currencies (code-ordered) as a member-facing directory.</summary>
    Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(ulong guildId, CancellationToken ct = default);
}

/// <inheritdoc cref="ICurrencyReadService"/>
public class CurrencyReadService(MusterDbContext db) : ICurrencyReadService
{
    public async Task<IReadOnlyList<LeaderboardEntry>> GetSeasonLeaderboardAsync(
        ulong guildId, int top = 25, CancellationToken ct = default)
    {
        var season = await db.FindActiveSeasonAsync(guildId, ct);
        var points = await db.FindPointsAsync(guildId, ct);

        if (season is null || points is null)
        {
            return [];
        }

        var rows = await db.TopWalletBalancesAsync(guildId, points.Id, season.Id, top, ct);
        return rows.Select(r => new LeaderboardEntry(r.UserId, r.Total)).ToList();
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(
        ulong guildId, string code, int top = 25, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return [];
        }

        Guid? seasonId = currency.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
        var rows = await db.TopWalletBalancesAsync(guildId, currency.Id, seasonId, top, ct);
        return rows.Select(r => new LeaderboardEntry(r.UserId, r.Total)).ToList();
    }

    public async Task<IReadOnlyList<LedgerHistoryEntry>> GetMemberHistoryAsync(
        ulong guildId, ulong userId, string? code, int skip, int take, CancellationToken ct = default)
    {
        Guid? currencyId = null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            var currency = await db.FindCurrencyAsync(guildId, code, ct);
            if (currency is null)
            {
                return [];
            }

            currencyId = currency.Id;
        }

        var rows = await db.MemberLedgerAsync(guildId, userId, currencyId, skip, take, ct);
        var codes = await db.CurrencyCodeMapAsync(guildId, ct);
        return rows
            .Select(r => new LedgerHistoryEntry(codes.GetValueOrDefault(r.CurrencyId, "?"), r.Amount, r.SourceType.ToString(), r.OccurredAt, r.Reason))
            .ToList();
    }

    public async Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(ulong guildId, CancellationToken ct = default)
    {
        var currencies = await db.ListCurrenciesAsync(guildId, ct);
        return currencies
            .OrderBy(c => c.Code)
            .Select(c => new CurrencyInfo(c.Code, c.Name, c.IsSpendable, c.IsSeasonal))
            .ToList();
    }

    public async Task<IReadOnlyList<WalletBalance>> GetWalletsAsync(
        ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var activeSeasonId = await db.ActiveSeasonIdAsync(guildId, ct);
        var currencies = await db.ListCurrenciesAsync(guildId, ct);
        var cache = await db.WalletBalancesAsync(guildId, userId, ct);

        var balances = new List<WalletBalance>(currencies.Count);
        foreach (var currency in currencies)
        {
            Guid? seasonFilter = currency.IsSeasonal ? activeSeasonId : null;
            var balance = cache.GetValueOrDefault((currency.Id, seasonFilter));
            balances.Add(new WalletBalance(currency.Code, currency.Name, balance, currency.IsSeasonal));
        }

        return balances;
    }
}
