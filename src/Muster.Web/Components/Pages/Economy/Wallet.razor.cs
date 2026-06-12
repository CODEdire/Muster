using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Enums;
using Muster.Infrastructure.Connectors;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using Wolverine;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy;

public partial class Wallet : IDisposable
{
    // --- Target member (admin / economy-manager can view another member's wallet via ?member=) ---
    [SupplyParameterFromQuery(Name = "member")] private string? MemberRaw { get; set; }
    private ulong _targetUserId;
    private bool _canManage;     // viewer is admin / economy-manager
    private bool _viewingOther;  // target != the signed-in user

    // Admin mint/adjust (shown in the transfer frame when viewing another member's wallet).
    private long _adjustAmount;
    private string _adjustReason = "";
    private bool _adjusting;

    // --- Currencies + selection ---
    private IReadOnlyList<CurrencyInfo> _currencies = [];
    // Account shows spendable wallet currencies only; points live in the dedicated Season + Points tabs.
    private IReadOnlyList<CurrencyInfo> _walletCurrencies = [];
    private bool _hasPoints;
    private string _sel = "";
    private string _displayName = "";
    private string? _avatarUrl;

    private CurrencyInfo? Selected => _currencies.FirstOrDefault(c => c.Code == _sel);
    private string SelUnit => Selected?.Code ?? "";
    private bool SelIsPoints => string.Equals(_sel, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>A non-spendable score currency (e.g. POINTS) only accrues — no spend tile, no out-flow.</summary>
    private bool IsScore => Selected is { Spendable: false };
    private int _rank;
    private int _holders;

    // --- KPIs / breakdown / sparkline (scoped to the selected currency, 30-day window) ---
    private WalletKpis _kpis = new(0, 0, 0, 0, 0, 0);
    private long _prevEarned;
    private long _prevSpent;
    private int _heldOrders;
    private IReadOnlyList<SourceFlow> _earned = [];
    private IReadOnlyList<BalancePoint> _series = [];
    private long EarnedMax => _earned.Count == 0 ? 1 : Math.Max(1, _earned.Max(s => s.Earned));

    // Blended-KPI helpers.
    private long BalancePrev => _kpis.Balance - _kpis.Net;
    private int TrendPct => BalancePrev <= 0 ? 0 : (int)(_kpis.Net * 100 / BalancePrev);
    private int AvailPct => _kpis.Balance <= 0 ? 100 : (int)(_kpis.Available * 100 / _kpis.Balance);
    private int HeldPct => _kpis.Balance <= 0 ? 0 : (int)(_kpis.Held * 100 / _kpis.Balance);
    private long FlowMax => Math.Max(1, Math.Max(_kpis.Earned, _kpis.Spent));
    private static int Delta(long now, long prev) => prev <= 0 ? (now > 0 ? 100 : 0) : (int)((now - prev) * 100 / prev);
    private int EarnedDelta => Delta(_kpis.Earned, _prevEarned);
    private int SpentDelta => Delta(_kpis.Spent, _prevSpent);

    // --- Quick transfer ---
    private IReadOnlyList<MemberOption> _recipients = [];
    private SendInput Send { get; set; } = new();
    private bool _sending;
    private bool _syncing;

    private IEnumerable<CurrencyInfo> Sendable => _currencies.Where(c => c is { Spendable: true, Transferable: true });

    // --- Ledger datagrid state ---
    private string _preset = "";
    private CurrencyLedgerSource? _source;
    private int? _sign;
    private ulong? _party;
    private IReadOnlyList<(ulong UserId, string Name, string? AvatarUrl)> _parties = [];
    private long? _expandedId;
    private (long In, long Out) _totals;
    private const int SearchDebounceMs = 350;
    private string? _searchBox;
    private CancellationTokenSource? _searchDebounce;
    private string _sortKey = "when";
    private bool _descending = true;
    private int _page = 1;
    private int _size = 25;
    private bool _loading;
    private PagedResult<MemberLedgerRow>? _activity;

    private int TotalPages => _activity?.TotalPages ?? 1;
    private string PagerLabel => $"Page {_page} of {TotalPages} · {(_activity?.Total ?? 0)} total";

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currencies = sp.GetRequiredService<ICurrencyReadService>();
        var members = sp.GetRequiredService<WebMemberService>();

        // An admin / economy-manager may view (and adjust) another member's wallet via ?member=.
        _canManage = await Auth.IsAdminAsync(GuildId, UserId) || await Auth.IsEconomyManagerAsync(GuildId, UserId);
        _targetUserId = ulong.TryParse(MemberRaw, out var m) && _canManage ? m : UserId;
        _viewingOther = _targetUserId != UserId;

        _currencies = await currencies.GetCurrenciesAsync(GuildId);
        _recipients = await members.GetRecipientsAsync(GuildId, UserId);

        _hasPoints = _currencies.Any(c => string.Equals(c.Code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase));
        _walletCurrencies = _currencies.Where(c => !string.Equals(c.Code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase)).ToList();

        // Open on the guild's primary/default wallet currency; fall back to the first spendable, then any.
        _sel = _walletCurrencies.FirstOrDefault(c => c.Primary)?.Code
            ?? _walletCurrencies.FirstOrDefault(c => c.Spendable)?.Code
            ?? _walletCurrencies.FirstOrDefault()?.Code
            ?? "";
        Send.Currency ??= Sendable.FirstOrDefault()?.Code;

        var detail = await members.GetAsync(GuildId, _targetUserId, historyCount: 1);
        _displayName = detail.DisplayName;
        _avatarUrl = detail.AvatarUrl;

        await LoadScopeAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State == AccessState.Ready && _activity is null && !string.IsNullOrEmpty(_sel))
        {
            await ReloadLedgerAsync();
        }
    }

    private async Task SelectCurrency(string code)
    {
        if (code == _sel)
        {
            return;
        }

        _sel = code;
        _page = 1;
        _source = null;
        _party = null;
        _expandedId = null;
        await LoadScopeAsync();
        await ReloadLedgerAsync();
    }

    /// <summary>Load the KPI bundle, earned-by-source and balance sparkline for the selected currency (30-day window).</summary>
    private async Task LoadScopeAsync()
    {
        if (string.IsNullOrEmpty(_sel))
        {
            return;
        }

        await using var scope = Scopes.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();

        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-30);

        _kpis = await wallet.GetKpisAsync(GuildId, _targetUserId, _sel, from, to);
        var prev = await wallet.GetKpisAsync(GuildId, _targetUserId, _sel, from.AddDays(-30), from);
        _prevEarned = prev.Earned;
        _prevSpent = prev.Spent;
        _heldOrders = _kpis.Held > 0 ? await wallet.GetHeldOrderCountAsync(GuildId, _targetUserId, _sel) : 0;
        var breakdown = await wallet.GetSourceBreakdownAsync(GuildId, _targetUserId, _sel, from, to);
        _earned = breakdown.Where(s => s.Earned > 0).OrderByDescending(s => s.Earned).Take(4).ToList();
        _series = await wallet.GetBalanceSeriesAsync(GuildId, _targetUserId, _sel, from, to);
        (_rank, _holders) = await wallet.GetWealthRankAsync(GuildId, _targetUserId, _sel);
        _parties = await wallet.GetCounterpartiesAsync(GuildId, _targetUserId, _sel);
    }

    private async Task ReloadLedgerAsync()
    {
        if (string.IsNullOrEmpty(_sel))
        {
            return;
        }

        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;

            // On-visit reconcile of any External/Hybrid balances (skipped for the seasonal points score).
            if (!SelIsPoints)
            {
                await sp.GetRequiredService<CurrencyConnectorSyncService>().SyncMemberDueAsync(GuildId, _targetUserId, force: false);
            }

            var (from, to) = ResolveRange(_preset);
            var sources = _source is { } s ? new[] { s } : null;

            if (SelIsPoints)
            {
                var points = sp.GetRequiredService<PointsReadService>();
                _activity = await points.GetHistoryPageAsync(
                    GuildId, _targetUserId, _searchBox, _sortKey, _descending, _page, _size, sources: sources, from: from, to: to, sign: _sign);
                _totals = await points.GetHistoryTotalsAsync(GuildId, _targetUserId, _searchBox, sources, from, to, _sign);
            }
            else
            {
                var wallet = sp.GetRequiredService<WalletReadService>();
                _activity = await wallet.GetHistoryPageAsync(
                    GuildId, _targetUserId, _sel, _searchBox, _sortKey, _descending, _page, _size,
                    sources: sources, from: from, to: to, sign: _sign, withRunningBalance: _sortKey == "when", counterpartyId: _party);
                _totals = await wallet.GetHistoryTotalsAsync(GuildId, _targetUserId, _sel, _searchBox, sources, from, to, _sign, _party);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnRange(ChangeEventArgs e)
    {
        _preset = (e.Value as string) ?? "";
        _page = 1;
        await ReloadLedgerAsync();
    }

    private async Task OnSource(ChangeEventArgs e)
    {
        var raw = e.Value as string;
        _source = string.IsNullOrEmpty(raw) || !Enum.TryParse<CurrencyLedgerSource>(raw, out var v) ? null : v;
        _page = 1;
        await ReloadLedgerAsync();
    }

    private async Task DebouncedSearchAsync()
    {
        _searchDebounce?.Cancel();
        var cts = _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(SearchDebounceMs, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!cts.IsCancellationRequested)
        {
            _page = 1;
            await ReloadLedgerAsync();
        }
    }

    private async Task SetSort(string column)
    {
        _descending = _sortKey == column ? !_descending : true;
        _sortKey = column;
        _page = 1;
        await ReloadLedgerAsync();
    }

    private async Task OnSize(ChangeEventArgs e)
    {
        _size = int.TryParse(e.Value as string, out var v) ? v : 25;
        _page = 1;
        await ReloadLedgerAsync();
    }

    private async Task Prev()
    {
        if (_page > 1)
        {
            _page--;
            await ReloadLedgerAsync();
        }
    }

    private async Task Next()
    {
        if (_page < TotalPages)
        {
            _page++;
            await ReloadLedgerAsync();
        }
    }

    private string Ind(string column) => _sortKey == column ? (_descending ? "▼" : "▲") : "";

    private async Task SetDirection(int? sign)
    {
        if (_sign == sign)
        {
            return;
        }

        _sign = sign;
        _page = 1;
        await ReloadLedgerAsync();
    }

    /// <summary>Day grouping only makes sense when the grid is date-ordered.</summary>
    private bool Grouped => _sortKey == "when";

    /// <summary>The running-balance column shows only when the read path returned it (single non-seasonal currency,
    /// date-sorted).</summary>
    private bool ShowBalanceCol => _activity is { Items.Count: > 0 } && _activity.Items[0].BalanceAfter.HasValue;
    private int LedgerCols => ShowBalanceCol ? 6 : 5;

    private async Task OnParty(ChangeEventArgs e)
    {
        var raw = e.Value as string;
        _party = string.IsNullOrEmpty(raw) || !ulong.TryParse(raw, out var v) ? null : v;
        _page = 1;
        await ReloadLedgerAsync();
    }

    private void ToggleDetail(long id) => _expandedId = _expandedId == id ? null : id;

    /// <summary>Generic counterparty label for entries with no member counterparty (shop / system awards).</summary>
    private static string PartyLabel(MemberLedgerRow r) =>
        r.Source is CurrencyLedgerSource.Shop or CurrencyLedgerSource.ShopFee ? "Shop" : "Guild";

    /// <summary>Export the current filtered ledger view to CSV (whole filtered set, not just the page).</summary>
    private async Task ExportCsvAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var (from, to) = ResolveRange(_preset);
        var sources = _source is { } s ? new[] { s } : null;

        IReadOnlyList<MemberLedgerRow> rows = SelIsPoints
            ? await sp.GetRequiredService<PointsReadService>().GetHistoryForExportAsync(GuildId, _targetUserId, _searchBox, sources, from, to, _sign)
            : await sp.GetRequiredService<WalletReadService>().GetHistoryForExportAsync(GuildId, _targetUserId, _sel, _searchBox, sources, from, to, _sign, _party);

        var sb = new StringBuilder();
        sb.AppendLine("When (UTC),Currency,Source,Party,Amount,Reason");
        foreach (var r in rows)
        {
            sb.Append(r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(r.Currency)).Append(',')
              .Append(Csv(SourceLabel(r.Source))).Append(',')
              .Append(Csv(r.CounterpartyName ?? PartyLabel(r))).Append(',')
              .Append(r.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .AppendLine(Csv(r.Reason));
        }

        var name = $"wallet-{_sel}-{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd}.csv";
        await JS.InvokeVoidAsync("musterDownload", name, sb.ToString(), "text/csv;charset=utf-8");
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    /// <summary>The current page's rows grouped by day (preserving the page's order), with each day's visible net.</summary>
    private IReadOnlyList<(DateOnly Date, long Net, List<MemberLedgerRow> Rows)> LedgerGroups()
    {
        if (_activity is null)
        {
            return [];
        }

        return _activity.Items
            .GroupBy(r => DateOnly.FromDateTime(r.OccurredAt.UtcDateTime))
            .Select(g => (g.Key, g.Sum(x => x.Amount), g.ToList()))
            .ToList();
    }

    /// <summary>One rendered ledger line: the row, plus a day-group header to emit before it when the day changes.</summary>
    private record LedgerLine(MemberLedgerRow Row, string? GroupLabel, long GroupNet);

    /// <summary>The page's rows flattened for rendering, with day-group headers inlined when date-sorted.</summary>
    private IReadOnlyList<LedgerLine> LedgerLines()
    {
        if (_activity is null)
        {
            return [];
        }

        var net = Grouped ? LedgerGroups().ToDictionary(g => g.Date, g => g.Net) : null;
        var lines = new List<LedgerLine>(_activity.Items.Count);
        DateOnly? last = null;
        foreach (var h in _activity.Items)
        {
            string? label = null;
            long groupNet = 0;
            if (Grouped)
            {
                var d = DateOnly.FromDateTime(h.OccurredAt.UtcDateTime);
                if (last != d)
                {
                    last = d;
                    label = DateLabel(d);
                    groupNet = net![d];
                }
            }

            lines.Add(new LedgerLine(h, label, groupNet));
        }

        return lines;
    }

    private static string DateLabel(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (d == today)
        {
            return "Today";
        }

        return d == today.AddDays(-1) ? "Yesterday" : d.ToString("ddd, d MMM");
    }

    /// <summary>Balance sparkline as an SVG polyline points string over a 100×30 viewbox (empty when too few points).</summary>
    private string SparkPoints()
    {
        if (_series.Count < 2)
        {
            return "";
        }

        long min = _series.Min(p => p.Balance);
        long max = _series.Max(p => p.Balance);
        double range = max - min == 0 ? 1 : max - min;
        int n = _series.Count;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            double x = i * (100.0 / (n - 1));
            double y = 29 - (_series[i].Balance - min) / range * 28;
            sb.Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
              .Append(y.ToString("0.#", CultureInfo.InvariantCulture)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    private async Task SendAsync()
    {
        if (_sending)
        {
            return;
        }

        if (Send.RecipientId == 0)
        {
            Message = "Choose who to send to.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Send.Currency))
        {
            Message = "Choose a currency.";
            return;
        }

        if (Send.Amount <= 0)
        {
            Message = "Amount must be greater than zero.";
            return;
        }

        _sending = true;
        try
        {
            var toName = _recipients.FirstOrDefault(r => r.UserId == Send.RecipientId)?.DisplayName ?? $"member {Send.RecipientId}";
            var amount = Send.Amount;
            var code = Send.Currency!.ToUpperInvariant();
            var reason = string.IsNullOrWhiteSpace(Send.Reason) ? "Wallet transfer" : Send.Reason!.Trim();

            var result = await Bus.InvokeAsync<Result>(
                new TransferCurrency(GuildId, UserId, code, UserId, Send.RecipientId, amount, reason));

            Message = result.Ok
                ? $"Sent {amount:N0} {code} to {toName}."
                : result.Status switch
                {
                    "InsufficientFunds" => "You don't have enough for that.",
                    "CurrencyNotFound" => "That currency doesn't exist here.",
                    "NotTransferable" => "That currency can't be transferred.",
                    "Forbidden" => "You're not allowed to do that.",
                    var other => other,
                };

            if (result.Ok)
            {
                Send = new SendInput { Currency = code };
            }
        }
        finally
        {
            _sending = false;
            await LoadScopeAsync();
            await ReloadLedgerAsync();
        }
    }

    /// <summary>Admin / economy-manager mint or adjust on the viewed member's selected currency (signed delta).</summary>
    private async Task AdjustAsync()
    {
        if (_adjusting || !_canManage || !_viewingOther)
        {
            return;
        }

        if (_adjustAmount == 0)
        {
            Message = "Amount must be non-zero.";
            return;
        }

        _adjusting = true;
        try
        {
            var reason = string.IsNullOrWhiteSpace(_adjustReason) ? "Wallet adjustment" : _adjustReason.Trim();
            var result = await Bus.InvokeAsync<Result>(new AdjustCurrency(GuildId, UserId, _sel, _targetUserId, _adjustAmount, reason));

            Message = result.Ok
                ? $"Applied {_adjustAmount:+#,##0;-#,##0} {_sel} to {_displayName}."
                : result.Status switch
                {
                    "Forbidden" => "You're not allowed to do that.",
                    "CurrencyNotFound" => "That currency doesn't exist here.",
                    "InsufficientFunds" => "That would take the member below zero.",
                    var other => other,
                };

            if (result.Ok)
            {
                _adjustAmount = 0;
                _adjustReason = "";
                await LoadScopeAsync();
                await ReloadLedgerAsync();
            }
        }
        finally
        {
            _adjusting = false;
        }
    }

    private async Task ForceSyncAsync()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<CurrencyConnectorSyncService>();
            var synced = await sync.SyncMemberDueAsync(GuildId, _targetUserId, force: true);
            Message = synced > 0 ? $"Synced {synced} balance(s) from connected economies." : "No connected balances to sync.";
        }
        finally
        {
            _syncing = false;
            await LoadScopeAsync();
            await ReloadLedgerAsync();
        }
    }

    public void Dispose()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }

    public class SendInput
    {
        public ulong RecipientId { get; set; }
        public string? Currency { get; set; }
        public long Amount { get; set; }
        public string? Reason { get; set; }
    }
}
