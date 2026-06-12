using Microsoft.EntityFrameworkCore;
using Muster.Domain;
using Muster.Domain.Enums;
using Muster.Infrastructure.Discord;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Tracking;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Web;

/// <summary>Headline wallet figures for one currency over a window: <see cref="Available"/> = <see cref="Balance"/>
/// − <see cref="Held"/> (shop escrow); <see cref="Net"/> = <see cref="Earned"/> − <see cref="Spent"/>.</summary>
public record WalletKpis(long Balance, long Held, long Available, long Earned, long Spent, long Net);

/// <summary>A point on the balance-over-time series (running balance at the end of <see cref="Date"/>).</summary>
public record BalancePoint(DateTimeOffset Date, long Balance);

/// <summary>Earned/spent totals for one calendar month — the cash-flow-by-month chart.</summary>
public record MonthFlow(int Year, int Month, long Earned, long Spent);

/// <summary>Earned/spent totals for one ledger source over a window — the by-source breakdowns.</summary>
public record SourceFlow(CurrencyLedgerSource Source, long Earned, long Spent);

/// <summary>
/// The Wallet surface: every currency in the guild <b>except POINTS</b>. POINTS lives behind
/// <see cref="PointsReadService"/>. Filter is applied at SQL where possible (paged ledger reads); list reads
/// (currencies/wallets) post-filter the short result. Same storage as the rest of the currency stack.
/// </summary>
public class WalletReadService(MusterDbContext db, ICurrencyReadService scores)
{
    /// <summary>The wallet's currencies — every guild currency except POINTS.</summary>
    public async Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(ulong guildId, CancellationToken ct = default)
    {
        var all = await scores.GetCurrenciesAsync(guildId, ct);
        return all.Where(c => c.Code != CurrencyCodes.PointsCode).ToList();
    }

    /// <summary>A member's balances on every wallet currency (POINTS hidden).</summary>
    public async Task<IReadOnlyList<WalletBalance>> GetWalletsAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var all = await scores.GetWalletsAsync(guildId, userId, ct);
        return all.Where(w => w.CurrencyCode != CurrencyCodes.PointsCode).ToList();
    }

    /// <summary>Supply analytics for one wallet currency. Returns null for unknown or POINTS.</summary>
    public async Task<CurrencySupply?> GetSupplyAsync(ulong guildId, string code, CancellationToken ct = default)
    {
        if (string.Equals(code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await scores.GetSupplyAsync(guildId, code, ct);
    }

    /// <summary>Paged ledger history for the wallet datagrid (POINTS excluded at query time).
    /// <paramref name="code"/> null/blank = all wallet currencies.</summary>
    public async Task<PagedResult<MemberLedgerRow>> GetHistoryPageAsync(
        ulong guildId, ulong userId, string? code, string? search, string sortKey, bool descending,
        int page, int pageSize, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        // Resolve POINTS once so the wallet surface can never accidentally surface a points row.
        var points = await db.FindCurrencyAsync(guildId, CurrencyCodes.PointsCode, ct);
        var pointsId = points?.Id;

        Guid? currencyId = null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (string.Equals(code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResult<MemberLedgerRow>([], page, pageSize, 0);
            }

            var currency = await db.FindCurrencyAsync(guildId, code, ct);
            if (currency is null)
            {
                return new PagedResult<MemberLedgerRow>([], page, pageSize, 0);
            }

            currencyId = currency.Id;
        }

        var size = Math.Clamp(pageSize, 1, 100);
        var p = Math.Max(page, 1);
        var skip = (p - 1) * size;

        var (rows, total) = await db.MemberLedgerPagedAsync(
            guildId, userId, currencyId, search, sortKey, descending, skip, size, ct,
            excludeCurrencyId: pointsId, sources: sources, from: from, to: to);

        var codes = await db.CurrencyCodeMapAsync(guildId, ct);
        var items = rows
            .Select(r => new MemberLedgerRow(codes.GetValueOrDefault(r.CurrencyId, "?"), r.Amount, r.SourceType, r.OccurredAt, r.Reason))
            .ToList();

        return new PagedResult<MemberLedgerRow>(items, p, size, total);
    }

    /// <summary>Paged top holders for one wallet currency (escrow excluded). Empty for unknown or POINTS.</summary>
    public async Task<PagedResult<LeaderboardRow>> GetTopHoldersPageAsync(
        ulong guildId, string code, int page, int pageSize, CancellationToken ct = default)
    {
        if (string.Equals(code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
        {
            return new PagedResult<LeaderboardRow>([], page, pageSize, 0);
        }

        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return new PagedResult<LeaderboardRow>([], page, pageSize, 0);
        }

        Guid? seasonId = currency.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
        var size = Math.Clamp(pageSize, 1, 100);
        var p = Math.Max(page, 1);
        var skip = (p - 1) * size;

        var (rows, total) = await db.TopWalletBalancesPagedAsync(
            guildId, currency.Id, seasonId, CurrencyService.EscrowAccountUserId, skip, size, ct);

        var ids = rows.Select(r => r.UserId).ToList();
        var users = await db.UserDisplayMapAsync(ids, ct);

        var items = rows
            .Select((r, i) =>
            {
                var u = users.GetValueOrDefault(r.UserId);
                return new LeaderboardRow(
                    skip + i + 1, r.UserId,
                    u.Name ?? r.UserId.ToString(),
                    r.Balance,
                    DiscordCdn.AvatarUrl(r.UserId, u.AvatarHash));
            })
            .ToList();

        return new PagedResult<LeaderboardRow>(items, p, size, total);
    }

    /// <summary>Paged guild-wide ledger movements for one wallet currency (POINTS hidden if asked for).</summary>
    public async Task<PagedResult<MovementRow>> GetMovementsPageAsync(
        ulong guildId, string code, string? search, string sortKey, bool descending,
        int page, int pageSize, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        if (string.Equals(code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
        {
            return new PagedResult<MovementRow>([], page, pageSize, 0);
        }

        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return new PagedResult<MovementRow>([], page, pageSize, 0);
        }

        var size = Math.Clamp(pageSize, 1, 100);
        var p = Math.Max(page, 1);
        var skip = (p - 1) * size;

        var (rows, total) = await db.GuildLedgerPagedAsync(
            guildId, currency.Id, search, sortKey, descending, skip, size, ct,
            sources: sources, from: from, to: to);
        var ids = rows.Select(r => r.UserId).Distinct().ToList();
        var users = await db.UserDisplayMapAsync(ids, ct);
        string Name(ulong id) => id == CurrencyService.EscrowAccountUserId
            ? "Escrow (house)"
            : users.TryGetValue(id, out var u) ? u.Name : id.ToString();
        string? Avatar(ulong id) => id == CurrencyService.EscrowAccountUserId
            ? null
            : users.TryGetValue(id, out var u) ? DiscordCdn.AvatarUrl(id, u.AvatarHash) : null;

        var codes = await db.CurrencyCodeMapAsync(guildId, ct);
        var items = rows
            .Select(r => new MovementRow(
                r.UserId, Name(r.UserId), Avatar(r.UserId), codes.GetValueOrDefault(r.CurrencyId, "?"),
                r.Amount, r.SourceType, r.OccurredAt, r.Reason))
            .ToList();

        return new PagedResult<MovementRow>(items, p, size, total);
    }

    // --- Wallet analytics (KPIs, balance-over-time, cash flow, source breakdown, escrow split) ---

    /// <summary>Resolve a currency code to its id + active-season scope. Null when the currency doesn't exist.</summary>
    private async Task<(Guid Id, Guid? SeasonId)?> ResolveScopeAsync(ulong guildId, string code, CancellationToken ct)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return null;
        }

        Guid? seasonId = currency.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
        return (currency.Id, seasonId);
    }

    /// <summary>Headline figures for one currency over <paramref name="from"/>..<paramref name="to"/>: balance,
    /// shop-escrow held, available (= balance − held), and earned/spent/net for the window.</summary>
    public async Task<WalletKpis> GetKpisAsync(
        ulong guildId, ulong userId, string code, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct) is not { } scope)
        {
            return new WalletKpis(0, 0, 0, 0, 0, 0);
        }

        var balance = await db.BalanceAsync(guildId, userId, scope.Id, scope.SeasonId, ct);
        var held = await db.MemberHeldFundsAsync(guildId, userId, scope.Id, ct);
        var (earned, spent) = await db.PeriodFlowAsync(guildId, userId, scope.Id, scope.SeasonId, from, to, ct);
        return new WalletKpis(balance, held, balance - held, earned, spent, earned - spent);
    }

    /// <summary>How many open shop orders are holding this currency for the member (the "N open orders" hint).</summary>
    public async Task<int> GetHeldOrderCountAsync(ulong guildId, ulong userId, string code, CancellationToken ct = default)
        => await ResolveScopeAsync(guildId, code, ct) is { } scope
            ? await db.MemberHeldOrderCountAsync(guildId, userId, scope.Id, ct)
            : 0;

    /// <summary>Balance-over-time: running balance at the end of each day that had movement in the window, seeded
    /// from the opening balance just before <paramref name="from"/>.</summary>
    public async Task<IReadOnlyList<BalancePoint>> GetBalanceSeriesAsync(
        ulong guildId, ulong userId, string code, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct) is not { } scope)
        {
            return [];
        }

        var running = await db.BalanceAsOfAsync(guildId, userId, scope.Id, scope.SeasonId, from, ct);
        var daily = await db.DailyNetSeriesAsync(guildId, userId, scope.Id, scope.SeasonId, from, to, ct);

        var points = new List<BalancePoint>(daily.Count);
        foreach (var d in daily)
        {
            running += d.Net;
            points.Add(new BalancePoint(new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero), running));
        }

        return points;
    }

    /// <summary>Earned/spent per calendar month over the window — the cash-flow-by-month chart.</summary>
    public async Task<IReadOnlyList<MonthFlow>> GetCashFlowAsync(
        ulong guildId, ulong userId, string code, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct) is not { } scope)
        {
            return [];
        }

        var rows = await db.MonthlyCashFlowAsync(guildId, userId, scope.Id, scope.SeasonId, from, to, ct);
        return rows.Select(r => new MonthFlow(r.Year, r.Month, r.Earned, r.Spent)).ToList();
    }

    /// <summary>Earned/spent per ledger source over the window — the earned-by-source / spent-by-source breakdowns.</summary>
    public async Task<IReadOnlyList<SourceFlow>> GetSourceBreakdownAsync(
        ulong guildId, ulong userId, string code, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct) is not { } scope)
        {
            return [];
        }

        var rows = await db.SourceBreakdownAsync(guildId, userId, scope.Id, scope.SeasonId, from, to, ct);
        return rows.Select(r => new SourceFlow(r.Source, r.Earned, r.Spent)).ToList();
    }
}
