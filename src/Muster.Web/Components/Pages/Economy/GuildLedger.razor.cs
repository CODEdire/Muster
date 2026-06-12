using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildLedger : IDisposable
{
    // Read-only ledger view — EconomyManager + Auditor. Admin passes implicitly. Bulk award is admin-only.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    private const int PageSize = 25;
    private const int SearchDebounceMs = 350;

    [SupplyParameterFromQuery(Name = "cur")] private string? Cur { get; set; }

    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _code = "";
    private bool _loading;

    private CurrencySupply? _supply;

    // Journal state.
    private PagedResult<JournalRow>? _journal;
    private (long In, long Out) _totals;
    private int _page = 1;
    private string? _searchBox;
    private string _preset = "";
    private CurrencyLedgerSource? _source;
    private int? _sign;
    private string? _account;
    private CancellationTokenSource? _searchDebounce;

    private int TotalPages => _journal?.TotalPages ?? 1;
    private bool Reconciles => _supply is { } s && s.Minted == s.Circulating + s.Escrow + s.Removed;

    // Bulk award (admin only).
    private bool _isAdmin;
    private IReadOnlyList<BulkRoleOption> _roles = [];
    private string _bulkRoleId = "";
    private long _bulkDelta;
    private string _bulkReason = "";
    private bool _bulkPreviewing;
    private bool _bulkQueuing;
    private bool _bulkConfirm;
    private int _bulkConfirmCount;
    private string? _bulkMessage;
    private CurrencyBulkBatch? _bulkBatch;

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        _isAdmin = await Auth.IsAdminAsync(GuildId, UserId);
        _currencies = await sp.GetRequiredService<WalletReadService>().GetCurrenciesAsync(GuildId);
        if (!string.IsNullOrWhiteSpace(Cur) && _currencies.Any(c => c.Code == Cur))
        {
            _code = Cur!;
        }
        else if (string.IsNullOrWhiteSpace(_code) || _currencies.All(c => c.Code != _code))
        {
            _code = _currencies.FirstOrDefault(c => c.Primary)?.Code ?? _currencies.FirstOrDefault()?.Code ?? "";
        }

        if (_isAdmin)
        {
            _roles = await sp.GetRequiredService<WebMemberService>().GetRoleOptionsAsync(GuildId);
        }

        await ReloadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State == AccessState.Ready && _journal is null && !string.IsNullOrWhiteSpace(_code))
        {
            await ReloadAsync();
        }
    }

    private async Task SelectCurrency(string code)
    {
        if (code == _code)
        {
            return;
        }

        _code = code;
        _page = 1;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (string.IsNullOrWhiteSpace(_code))
        {
            return;
        }

        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
            _supply = await wallet.GetSupplyAsync(GuildId, _code);

            var (from, to) = ResolveRange(_preset);
            var sources = _source is { } s ? new[] { s } : null;
            _journal = await wallet.GetJournalAsync(GuildId, _code, _searchBox, _page, PageSize, sources, from, to, _sign, _account);
            _totals = await wallet.GetJournalTotalsAsync(GuildId, _code, _searchBox, sources, from, to, _sign, _account);
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- Journal filters ----

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

    private async Task OnAccount(ChangeEventArgs e)
    {
        var raw = e.Value as string;
        _account = string.IsNullOrEmpty(raw) ? null : raw;
        _page = 1;
        await ReloadAsync();
    }

    private async Task SetDirection(int? sign)
    {
        if (_sign == sign)
        {
            return;
        }

        _sign = sign;
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

    /// <summary>Day-grouped journal rows for rendering (date header before each new day).</summary>
    private IReadOnlyList<(DateOnly? Header, JournalRow Row)> Lines()
    {
        if (_journal is null)
        {
            return [];
        }

        var list = new List<(DateOnly?, JournalRow)>(_journal.Items.Count);
        DateOnly? last = null;
        foreach (var r in _journal.Items)
        {
            var d = DateOnly.FromDateTime(r.OccurredAt.UtcDateTime);
            list.Add((last != d ? d : null, r));
            last = d;
        }

        return list;
    }

    private static string DateLabel(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (d == today)
        {
            return "Today";
        }

        return d == today.AddDays(-1) ? "Yesterday" : d.ToString("ddd, d MMM yyyy");
    }

    // ---- Bulk handlers ----

    private void OnBulkRole(ChangeEventArgs e)
    {
        _bulkRoleId = (e.Value as string) ?? "";
        _bulkConfirm = false;
        _bulkMessage = null;
    }

    private void OnBulkDelta(ChangeEventArgs e)
    {
        _bulkDelta = long.TryParse(e.Value as string, out var v) ? v : 0;
        _bulkConfirm = false;
        _bulkMessage = null;
    }

    private void OnBulkReason(ChangeEventArgs e)
    {
        _bulkReason = (e.Value as string) ?? "";
        _bulkConfirm = false;
        _bulkMessage = null;
    }

    private async Task PreviewBulkAsync()
    {
        _bulkMessage = null;
        if (_bulkDelta == 0) { _bulkMessage = "Amount must be non-zero."; return; }
        if (string.IsNullOrWhiteSpace(_bulkReason)) { _bulkMessage = "A reason is required."; return; }
        if (!ulong.TryParse(_bulkRoleId, out var roleId)) { _bulkMessage = "Pick a role."; return; }

        _bulkPreviewing = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var members = scope.ServiceProvider.GetRequiredService<WebMemberService>();
            var targets = await members.GetRoleTargetsAsync(GuildId, roleId);
            if (targets.Count == 0)
            {
                _bulkMessage = "That role has no members.";
                _bulkConfirm = false;
                return;
            }

            _bulkConfirmCount = targets.Count;
            _bulkConfirm = true;
        }
        finally
        {
            _bulkPreviewing = false;
        }
    }

    private async Task QueueBulkAsync()
    {
        if (!ulong.TryParse(_bulkRoleId, out var roleId))
        {
            _bulkMessage = "Pick a role.";
            return;
        }

        _bulkQueuing = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var members = sp.GetRequiredService<WebMemberService>();
            var bulk = sp.GetRequiredService<ICurrencyBulkService>();

            var targets = await members.GetRoleTargetsAsync(GuildId, roleId);
            var result = await bulk.QueueAsync(GuildId, UserId, _code, _bulkDelta, _bulkReason, targets);
            _bulkMessage = result.Message;
            _bulkConfirm = false;

            if (result.Ok && result.BatchId is { } batchId)
            {
                await sp.GetRequiredService<AuditService>()
                    .RecordBulkQueueAsync(GuildId, UserId, _code, _bulkDelta, targets.Count, _bulkReason, batchId);
                _bulkBatch = await bulk.GetAsync(GuildId, batchId);
                _bulkDelta = 0;
                _bulkReason = "";
                await ReloadAsync();
            }
        }
        finally
        {
            _bulkQueuing = false;
        }
    }

    private async Task RefreshBatchAsync()
    {
        if (_bulkBatch is null) return;
        await using var scope = Scopes.CreateAsyncScope();
        _bulkBatch = await scope.ServiceProvider.GetRequiredService<ICurrencyBulkService>().GetAsync(GuildId, _bulkBatch.Id);
    }

    public void Dispose()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }
}
