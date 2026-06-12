using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
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
    // Activity grid local state.
    private string? _code;
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

    private string _displayName = "";
    private IReadOnlyList<WalletBalance> _wallets = [];
    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private IReadOnlyList<CurrencyInfo> _spendable = [];
    private IReadOnlyList<MemberOption> _recipients = [];
    private PagedResult<MemberLedgerRow>? _activity;
    private bool _sending;
    private bool _syncing;

    private SendInput Send { get; set; } = new();

    private int TotalPages => _activity?.TotalPages ?? 1;

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var wallet = sp.GetRequiredService<WalletReadService>();
        var members = sp.GetRequiredService<WebMemberService>();

        _currencies = await wallet.GetCurrenciesAsync(GuildId);
        _spendable = _currencies.Where(c => c.Spendable).ToList();
        _recipients = await members.GetRecipientsAsync(GuildId, UserId);
        Send.Currency ??= _spendable.FirstOrDefault()?.Code;

        // Display name + initial activity load.
        var detail = await members.GetAsync(GuildId, UserId, historyCount: 1);
        _displayName = detail.DisplayName;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State == AccessState.Ready && _activity is null)
        {
            await ReloadAsync();
        }
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var wallet = sp.GetRequiredService<WalletReadService>();
            var sync = sp.GetRequiredService<CurrencyConnectorSyncService>();

            // On-visit reconcile of any External/Hybrid balances, throttled to once per 5 minutes per wallet.
            await sync.SyncMemberDueAsync(GuildId, UserId, force: false);

            _wallets = await wallet.GetWalletsAsync(GuildId, UserId);

            var (from, to) = ResolveRange(_preset);
            var sources = _source is { } s ? new[] { s } : null;
            _activity = await wallet.GetHistoryPageAsync(
                GuildId, UserId,
                string.IsNullOrWhiteSpace(_code) ? null : _code,
                _searchBox,
                _sortKey, _descending,
                _page, _size,
                sources: sources, from: from, to: to);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnCode(ChangeEventArgs e)
    {
        var v = (e.Value as string)?.Trim();
        _code = string.IsNullOrEmpty(v) ? null : v;
        _page = 1;
        await ReloadAsync();
    }

    private async Task OnRange(ChangeEventArgs e)
    {
        _preset = (e.Value as string) ?? "";
        _page = 1;
        await ReloadAsync();
    }

    private async Task OnSource(ChangeEventArgs e)
    {
        var raw = e.Value as string;
        _source = string.IsNullOrEmpty(raw) || !Enum.TryParse<CurrencyLedgerSource>(raw, out var v) ? null : v;
        _page = 1;
        await ReloadAsync();
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
            await ReloadAsync();
        }
    }

    private async Task SetSort(string column)
    {
        _descending = _sortKey == column ? !_descending : true;
        _sortKey = column;
        _page = 1;
        await ReloadAsync();
    }

    private async Task OnSize(ChangeEventArgs e)
    {
        _size = int.TryParse(e.Value as string, out var v) ? v : 25;
        _page = 1;
        await ReloadAsync();
    }

    private async Task Prev()
    {
        if (_page > 1)
        {
            _page--;
            await ReloadAsync();
        }
    }

    private async Task Next()
    {
        if (_page < TotalPages)
        {
            _page++;
            await ReloadAsync();
        }
    }

    private string Ind(string column) => _sortKey == column ? (_descending ? "▼" : "▲") : "";

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
            await ReloadAsync();
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
            await ReloadAsync();
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
