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

/// <summary>One source's contribution to the faucets (mint) or sinks (burn) side of the flow view.</summary>
public record FlowSource(CurrencyLedgerSource Source, long Total);

/// <summary>Faucets vs sinks over a window: mint (net-new currency from system awards) by source, burn (currency
/// destroyed in the sink) by source, the totals, net supply change and the resulting monthly inflation %.</summary>
public record FlowView(
    IReadOnlyList<FlowSource> Faucets, IReadOnlyList<FlowSource> Sinks,
    long Minted, long Burned, long Net, long Circulating, double InflationPct);

/// <summary>One month of mint/burn/net for the flow-over-time chart and KPI sparklines.</summary>
public record FlowMonth(int Year, int Month, long Minted, long Burned, long Net);

/// <summary>One balance-bracket bucket for the wealth-distribution histogram.</summary>
public record DistributionBracket(string Label, int Count);

/// <summary>Wealth-distribution stats for a currency: holders, median/mean, top-10% share, Gini, and the histogram.</summary>
public record DistributionView(int Holders, long Median, long Mean, int Top10Pct, double Gini, long Max, IReadOnlyList<DistributionBracket> Brackets);

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
        DateTimeOffset? from = null, DateTimeOffset? to = null, int? sign = null, bool withRunningBalance = false,
        ulong? counterpartyId = null, CancellationToken ct = default)
    {
        // Resolve POINTS once so the wallet surface can never accidentally surface a points row.
        var points = await db.FindCurrencyAsync(guildId, CurrencyCodes.PointsCode, ct);
        var pointsId = points?.Id;

        Currency? currency = null;
        Guid? currencyId = null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (string.Equals(code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
            {
                return new PagedResult<MemberLedgerRow>([], page, pageSize, 0);
            }

            currency = await db.FindCurrencyAsync(guildId, code, ct);
            if (currency is null)
            {
                return new PagedResult<MemberLedgerRow>([], page, pageSize, 0);
            }

            currencyId = currency.Id;
        }

        var size = Math.Clamp(pageSize, 1, 100);
        var p = Math.Max(page, 1);
        var skip = (p - 1) * size;
        var codes = await db.CurrencyCodeMapAsync(guildId, ct);

        // Running balance only makes sense scoped to a single non-seasonal currency (escrow account excluded).
        var (rows, total) = withRunningBalance && currency is { IsSeasonal: false } && currencyId is { } cid
            ? await db.MemberLedgerPagedWithBalanceAsync(
                guildId, userId, cid, search, sortKey, descending, skip, size, ct,
                sources: sources, from: from, to: to, sign: sign, counterpartyId: counterpartyId)
            : await db.MemberLedgerPagedAsync(
                guildId, userId, currencyId, search, sortKey, descending, skip, size, ct,
                excludeCurrencyId: pointsId, sources: sources, from: from, to: to, sign: sign, counterpartyId: counterpartyId);

        return new PagedResult<MemberLedgerRow>(await ToRowsAsync(codes, rows, ct), p, size, total);
    }

    /// <summary>Map raw ledger projections to display rows, batch-resolving counterparty names/avatars.</summary>
    private async Task<List<MemberLedgerRow>> ToRowsAsync(IReadOnlyDictionary<Guid, string> codes, List<MemberLedgerProjection> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.CounterpartyId is not null).Select(r => r.CounterpartyId!.Value).Distinct().ToList();
        var users = ids.Count > 0 ? await db.UserDisplayMapAsync(ids, ct) : [];

        return rows.Select(r =>
        {
            string? name = null, avatar = null;
            if (r.CounterpartyId is { } cid && users.TryGetValue(cid, out var u))
            {
                name = u.Name;
                avatar = DiscordCdn.AvatarUrl(cid, u.AvatarHash);
            }

            return new MemberLedgerRow(
                codes.GetValueOrDefault(r.CurrencyId, "?"), r.Amount, r.SourceType, r.OccurredAt, r.Reason,
                r.BalanceAfter, r.Id, r.SourceId, r.CounterpartyId, name, avatar);
        }).ToList();
    }

    /// <summary>The member's transfer partners for a currency (for the party filter), resolved to name + avatar.</summary>
    public async Task<IReadOnlyList<(ulong UserId, string Name, string? AvatarUrl)>> GetCounterpartiesAsync(
        ulong guildId, ulong userId, string? code, CancellationToken ct = default)
    {
        Guid? currencyId = null;
        if (!string.IsNullOrWhiteSpace(code) && !string.Equals(code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
        {
            currencyId = (await db.FindCurrencyAsync(guildId, code, ct))?.Id;
        }

        var ids = await db.MemberCounterpartiesAsync(guildId, userId, currencyId, ct);
        if (ids.Count == 0)
        {
            return [];
        }

        var users = await db.UserDisplayMapAsync(ids, ct);
        return ids
            .Select(id => (id, users.TryGetValue(id, out var u) ? u.Name : id.ToString(), users.TryGetValue(id, out var u2) ? DiscordCdn.AvatarUrl(id, u2.AvatarHash) : null))
            .OrderBy(x => x.Item2)
            .ToList();
    }

    /// <summary>All filtered wallet-ledger rows (POINTS excluded), capped, for a CSV export of the current view.</summary>
    public async Task<IReadOnlyList<MemberLedgerRow>> GetHistoryForExportAsync(
        ulong guildId, ulong userId, string? code, string? search, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int? sign = null, ulong? counterpartyId = null, int cap = 10000, CancellationToken ct = default)
    {
        var points = await db.FindCurrencyAsync(guildId, CurrencyCodes.PointsCode, ct);
        var pointsId = points?.Id;

        Guid? currencyId = null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (string.Equals(code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var currency = await db.FindCurrencyAsync(guildId, code, ct);
            if (currency is null)
            {
                return [];
            }

            currencyId = currency.Id;
        }

        var rows = await db.MemberLedgerAllAsync(
            guildId, userId, currencyId, search, cap, ct,
            excludeCurrencyId: pointsId, sources: sources, from: from, to: to, sign: sign, counterpartyId: counterpartyId);

        var codes = await db.CurrencyCodeMapAsync(guildId, ct);
        return await ToRowsAsync(codes, rows, ct);
    }

    /// <summary>Σ in / Σ out for the same filter the ledger datagrid is showing (POINTS excluded; same code/sources/
    /// window/search/direction). Powers the ledger footer totals.</summary>
    public async Task<(long In, long Out)> GetHistoryTotalsAsync(
        ulong guildId, ulong userId, string? code, string? search, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int? sign = null, ulong? counterpartyId = null, CancellationToken ct = default)
    {
        var points = await db.FindCurrencyAsync(guildId, CurrencyCodes.PointsCode, ct);
        var pointsId = points?.Id;

        Guid? currencyId = null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (string.Equals(code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
            {
                return (0, 0);
            }

            var currency = await db.FindCurrencyAsync(guildId, code, ct);
            if (currency is null)
            {
                return (0, 0);
            }

            currencyId = currency.Id;
        }

        return await db.MemberLedgerTotalsAsync(
            guildId, userId, currencyId, search, ct,
            excludeCurrencyId: pointsId, sources: sources, from: from, to: to, sign: sign, counterpartyId: counterpartyId);
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
        string Name(ulong id) => id switch
        {
            CurrencyService.EscrowAccountUserId => "Escrow (house)",
            CurrencyService.BurnAccountUserId => "Burn (sink)",
            _ => users.TryGetValue(id, out var u) ? u.Name : id.ToString(),
        };
        string? Avatar(ulong id) => id is CurrencyService.EscrowAccountUserId or CurrencyService.BurnAccountUserId
            ? null
            : users.TryGetValue(id, out var u) ? DiscordCdn.AvatarUrl(id, u.AvatarHash) : null;
        static string Account(ulong id) => id switch
        {
            CurrencyService.EscrowAccountUserId => "escrow",
            CurrencyService.BurnAccountUserId => "burn",
            _ => "member",
        };

        var codes = await db.CurrencyCodeMapAsync(guildId, ct);
        var items = rows
            .Select(r => new MovementRow(
                r.UserId, Name(r.UserId), Avatar(r.UserId), codes.GetValueOrDefault(r.CurrencyId, "?"),
                r.Amount, r.SourceType, r.OccurredAt, r.Reason, Account(r.UserId)))
            .ToList();

        return new PagedResult<MovementRow>(items, p, size, total);
    }

    /// <summary>Σ minted / Σ burned for the guild-ledger movement filter — the books footer totals.</summary>
    public async Task<(long In, long Out)> GetMovementTotalsAsync(
        ulong guildId, string code, string? search, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        if (string.Equals(code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
        {
            return (0, 0);
        }

        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        return currency is null
            ? (0, 0)
            : await db.GuildLedgerTotalsAsync(guildId, currency.Id, search, ct, sources: sources, from: from, to: to);
    }

    // --- Wallet analytics (KPIs, balance-over-time, cash flow, source breakdown, escrow split) ---

    /// <summary>Resolve a currency code to its id + active-season scope. Null when the currency doesn't exist.</summary>
    private async Task<(Guid Id, Guid? SeasonId)?> ResolveScopeAsync(ulong guildId, string code, CancellationToken ct, Guid? seasonOverride = null)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return null;
        }

        // Seasonal currencies scope to a chosen season (the season picker) or, by default, the active one.
        Guid? seasonId = currency.IsSeasonal ? (seasonOverride ?? await db.ActiveSeasonIdAsync(guildId, ct)) : null;
        return (currency.Id, seasonId);
    }

    /// <summary>Headline figures for one currency over <paramref name="from"/>..<paramref name="to"/>: balance,
    /// shop-escrow held, available (= balance − held), and earned/spent/net for the window.</summary>
    public async Task<WalletKpis> GetKpisAsync(
        ulong guildId, ulong userId, string code, DateTimeOffset from, DateTimeOffset to, Guid? season = null, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct, season) is not { } scope)
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
        ulong guildId, ulong userId, string code, DateTimeOffset from, DateTimeOffset to, Guid? season = null, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct, season) is not { } scope)
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

    /// <summary>Wealth-distribution stats for one currency: holder count, median/mean, the share held by the top 10%,
    /// the Gini coefficient, and a balance-bracket histogram (6 linear buckets up to the richest holder).</summary>
    public async Task<DistributionView> GetDistributionAsync(ulong guildId, string code, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct) is not { } scope)
        {
            return new DistributionView(0, 0, 0, 0, 0, 0, []);
        }

        var balances = await db.GuildMemberBalancesAsync(guildId, scope.Id, scope.SeasonId, ct);
        if (balances.Count == 0)
        {
            return new DistributionView(0, 0, 0, 0, 0, 0, []);
        }

        balances.Sort();
        var n = balances.Count;
        var total = balances.Sum();
        var mean = total / n;
        var median = n % 2 == 1 ? balances[n / 2] : (balances[n / 2 - 1] + balances[n / 2]) / 2;

        var topCount = Math.Max(1, (int)Math.Ceiling(n * 0.1));
        long topSum = 0;
        for (var i = n - topCount; i < n; i++)
        {
            topSum += balances[i];
        }

        var top10 = total > 0 ? (int)(topSum * 100 / total) : 0;

        double weighted = 0;
        for (var i = 0; i < n; i++)
        {
            weighted += (i + 1) * (double)balances[i];
        }

        var gini = total > 0 ? Math.Round((2 * weighted) / (n * (double)total) - (n + 1.0) / n, 2) : 0;

        // Always 10 buckets from 0 to the richest holder, so the histogram has a consistent shape even with a single
        // member. The step auto-scales with the top balance; the last bucket is the "+" overflow.
        const int buckets = 10;
        var max = balances[^1];
        var size = Math.Max(1, (long)Math.Ceiling(max / (double)buckets));
        var counts = new int[buckets];
        foreach (var b in balances)
        {
            counts[(int)Math.Min(buckets - 1, b / size)]++;
        }

        var brackets = new List<DistributionBracket>(buckets);
        for (var i = 0; i < buckets; i++)
        {
            var lo = i * size;
            brackets.Add(new DistributionBracket(i == buckets - 1 ? $"{Kfmt(lo)}+" : $"{Kfmt(lo)}–{Kfmt(lo + size)}", counts[i]));
        }

        return new DistributionView(n, median, mean, top10, gini, max, brackets);
    }

    private static string Kfmt(long v) => v >= 1000 ? $"{v / 1000.0:0.#}k" : v.ToString();

    /// <summary>Ledger-derived top holders for a currency (escrow/burn excluded), resolved to name + avatar — stays
    /// correct even when the wallet cache is stale.</summary>
    public async Task<IReadOnlyList<LeaderboardRow>> GetTopHoldersLedgerAsync(ulong guildId, string code, int take, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct) is not { } scope)
        {
            return [];
        }

        var rows = await db.GuildTopHoldersLedgerAsync(guildId, scope.Id, scope.SeasonId, take, ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var users = await db.UserDisplayMapAsync(rows.Select(r => r.UserId).ToList(), ct);
        return rows
            .Select((r, i) => new LeaderboardRow(
                i + 1, r.UserId,
                users.TryGetValue(r.UserId, out var u) ? u.Name : r.UserId.ToString(),
                r.Total,
                users.TryGetValue(r.UserId, out var u2) ? DiscordCdn.AvatarUrl(r.UserId, u2.AvatarHash) : null))
            .ToList();
    }

    /// <summary>Mint sources — system awards that create net-new currency. Transfers and shop payouts are
    /// redistribution (not minting); checkpoints are carry-forward openings; all are excluded from the faucet total.</summary>
    private static readonly HashSet<CurrencyLedgerSource> MintSources =
    [
        CurrencyLedgerSource.TrackingSession, CurrencyLedgerSource.Quest, CurrencyLedgerSource.Muster,
        CurrencyLedgerSource.Event, CurrencyLedgerSource.Background, CurrencyLedgerSource.ManualAward,
        CurrencyLedgerSource.Connector, CurrencyLedgerSource.Adjustment,
    ];

    /// <summary>Faucets (minted, by source) vs sinks (burned, by source) over the window, with net supply change and
    /// the resulting monthly inflation %. Net-new vs destroyed: redistribution (transfers, shop payouts) is excluded.</summary>
    public async Task<FlowView> GetFlowAsync(ulong guildId, string code, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct) is not { } scope)
        {
            return new FlowView([], [], 0, 0, 0, 0, 0);
        }

        var mintMap = await db.GuildSourceEarnedAsync(guildId, scope.Id, scope.SeasonId, ct, from, to);
        var faucets = mintMap
            .Where(kv => MintSources.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new FlowSource(kv.Key, kv.Value))
            .ToList();

        var burnMap = await db.GuildBurnBySourceAsync(guildId, scope.Id, from, to, ct);
        var sinks = burnMap
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new FlowSource(kv.Key, kv.Value))
            .ToList();

        var minted = faucets.Sum(f => f.Total);
        var burned = sinks.Sum(s => s.Total);
        var net = minted - burned;
        var circulating = await db.GuildCirculatingAsOfAsync(guildId, scope.Id, scope.SeasonId, to, ct);
        var prior = circulating - net;
        var inflation = prior > 0 ? Math.Round(net * 100.0 / prior, 1) : 0;

        return new FlowView(faucets, sinks, minted, burned, net, circulating, inflation);
    }

    /// <summary>Minted / burned / net per calendar month across the window (zero-filled) — the flow-over-time chart,
    /// KPI sparklines and inflation-over-time line.</summary>
    public async Task<IReadOnlyList<FlowMonth>> GetFlowSeriesAsync(ulong guildId, string code, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct) is not { } scope)
        {
            return [];
        }

        var mint = await db.GuildMonthlyMintedAsync(guildId, scope.Id, scope.SeasonId, from, to, MintSources, ct);
        var burn = await db.GuildMonthlyBurnedAsync(guildId, scope.Id, from, to, ct);

        var list = new List<FlowMonth>();
        var cur = new DateTime(from.Year, from.Month, 1);
        var end = new DateTime(to.Year, to.Month, 1);
        while (cur <= end)
        {
            var m = mint.GetValueOrDefault((cur.Year, cur.Month), 0);
            var b = burn.GetValueOrDefault((cur.Year, cur.Month), 0);
            list.Add(new FlowMonth(cur.Year, cur.Month, m, b, m - b));
            cur = cur.AddMonths(1);
        }

        return list;
    }

    /// <summary>Circulating supply (member-held) at the end of each day with movement in the window — the guild
    /// treasury supply-over-time / candle chart. Seeded from the opening circulating balance before the window.</summary>
    public async Task<IReadOnlyList<BalancePoint>> GetSupplySeriesAsync(
        ulong guildId, string code, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct) is not { } scope)
        {
            return [];
        }

        var running = await db.GuildCirculatingAsOfAsync(guildId, scope.Id, scope.SeasonId, from, ct);
        var daily = await db.GuildCirculatingDailyNetAsync(guildId, scope.Id, scope.SeasonId, from, to, ct);

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
        ulong guildId, ulong userId, string code, DateTimeOffset from, DateTimeOffset to, Guid? season = null, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct, season) is not { } scope)
        {
            return [];
        }

        var rows = await db.MonthlyCashFlowAsync(guildId, userId, scope.Id, scope.SeasonId, from, to, ct);
        return rows.Select(r => new MonthFlow(r.Year, r.Month, r.Earned, r.Spent)).ToList();
    }

    /// <summary>Earned/spent per ledger source over the window — the earned-by-source / spent-by-source breakdowns.</summary>
    public async Task<IReadOnlyList<SourceFlow>> GetSourceBreakdownAsync(
        ulong guildId, ulong userId, string code, DateTimeOffset from, DateTimeOffset to, Guid? season = null, CancellationToken ct = default)
    {
        if (await ResolveScopeAsync(guildId, code, ct, season) is not { } scope)
        {
            return [];
        }

        var rows = await db.SourceBreakdownAsync(guildId, userId, scope.Id, scope.SeasonId, from, to, ct);
        return rows.Select(r => new SourceFlow(r.Source, r.Earned, r.Spent)).ToList();
    }

    /// <summary>A member's wealth rank for one currency (1-based) and the total holder count — the analytics
    /// "wealth rank" tile. Returns (0, 0) when the currency doesn't exist.</summary>
    public async Task<(int Rank, int Holders)> GetWealthRankAsync(ulong guildId, ulong userId, string code, Guid? season = null, CancellationToken ct = default)
        => await ResolveScopeAsync(guildId, code, ct, season) is { } scope
            ? await db.BalanceRankAsync(guildId, scope.Id, scope.SeasonId, userId, CurrencyService.EscrowAccountUserId, ct)
            : (0, 0);

    /// <summary>Seasons for the picker — empty unless the currency is seasonal (POINTS-style). Newest first.</summary>
    public async Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(ulong guildId, string code, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        return currency is { IsSeasonal: true } ? await db.SeasonsAsync(guildId, ct) : [];
    }

    /// <summary>A member's per-season totals for a seasonal currency (season-over-season chart), oldest season first.</summary>
    public async Task<IReadOnlyList<(SeasonInfo Season, long Total)>> GetSeasonTotalsAsync(ulong guildId, ulong userId, string code, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is not { IsSeasonal: true })
        {
            return [];
        }

        var seasons = await db.SeasonsAsync(guildId, ct);
        var totals = await db.MemberSeasonTotalsAsync(guildId, userId, currency.Id, ct);
        return seasons
            .OrderBy(s => s.StartsAt)
            .Select(s => (s, totals.GetValueOrDefault(s.Id, 0L)))
            .ToList();
    }
}
