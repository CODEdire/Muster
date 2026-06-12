using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
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
    // --- Currencies + selection ---
    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _sel = "";
    private string _displayName = "";
    private string? _avatarUrl;

    private CurrencyInfo? Selected => _currencies.FirstOrDefault(c => c.Code == _sel);
    private string SelUnit => Selected?.Code ?? "";
    private bool SelIsPoints => string.Equals(_sel, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase);

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

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var currencies = sp.GetRequiredService<ICurrencyReadService>();
        var members = sp.GetRequiredService<WebMemberService>();

        _currencies = await currencies.GetCurrenciesAsync(GuildId);
        _recipients = await members.GetRecipientsAsync(GuildId, UserId);

        // Open on the guild's primary/default currency; fall back to the first spendable, then any.
        _sel = _currencies.FirstOrDefault(c => c.Primary)?.Code
            ?? _currencies.FirstOrDefault(c => c.Spendable)?.Code
            ?? _currencies.FirstOrDefault()?.Code
            ?? "";
        Send.Currency ??= Sendable.FirstOrDefault()?.Code;

        var detail = await members.GetAsync(GuildId, UserId, historyCount: 1);
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

        _kpis = await wallet.GetKpisAsync(GuildId, UserId, _sel, from, to);
        var prev = await wallet.GetKpisAsync(GuildId, UserId, _sel, from.AddDays(-30), from);
        _prevEarned = prev.Earned;
        _prevSpent = prev.Spent;
        _heldOrders = _kpis.Held > 0 ? await wallet.GetHeldOrderCountAsync(GuildId, UserId, _sel) : 0;
        var breakdown = await wallet.GetSourceBreakdownAsync(GuildId, UserId, _sel, from, to);
        _earned = breakdown.Where(s => s.Earned > 0).OrderByDescending(s => s.Earned).Take(4).ToList();
        _series = await wallet.GetBalanceSeriesAsync(GuildId, UserId, _sel, from, to);
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
                await sp.GetRequiredService<CurrencyConnectorSyncService>().SyncMemberDueAsync(GuildId, UserId, force: false);
            }

            var (from, to) = ResolveRange(_preset);
            var sources = _source is { } s ? new[] { s } : null;

            _activity = SelIsPoints
                ? await sp.GetRequiredService<PointsReadService>().GetHistoryPageAsync(
                    GuildId, UserId, _searchBox, _sortKey, _descending, _page, _size, sources: sources, from: from, to: to)
                : await sp.GetRequiredService<WalletReadService>().GetHistoryPageAsync(
                    GuildId, UserId, _sel, _searchBox, _sortKey, _descending, _page, _size, sources: sources, from: from, to: to);
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
            var synced = await sync.SyncMemberDueAsync(GuildId, UserId, force: true);
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
