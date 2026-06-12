using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Membership;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using Muster.Web.Components.Shared;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildLedger : IDisposable
{
    // Read-only ledger view — EconomyManager + Auditor. Admin passes implicitly.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    private const int PageSize = 25;
    private const int SearchDebounceMs = 350;

    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _code = "";
    private string _tab = "holders";
    private bool _loading;

    private CurrencySupply? _supply;
    private PagedResult<LeaderboardRow>? _holders;
    private int _holdersPage = 1;

    private PagedResult<MovementRow>? _movement;
    private int _movePage = 1;
    private string? _moveSearchBox;
    private string _movePreset = "";
    private CurrencyLedgerSource? _moveSource;
    private string _moveSortKey = "when";
    private bool _moveDescending = true;
    private CancellationTokenSource? _moveSearchDebounce;

    // Bulk tab state (admin only).
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

    private int HoldersTotalPages => _holders?.TotalPages ?? 1;
    private int MoveTotalPages => _movement?.TotalPages ?? 1;

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        _isAdmin = await Auth.IsAdminAsync(GuildId, UserId);

        var wallet = sp.GetRequiredService<WalletReadService>();
        _currencies = await wallet.GetCurrenciesAsync(GuildId);
        if (string.IsNullOrWhiteSpace(_code) || _currencies.All(c => c.Code != _code))
        {
            _code = _currencies.FirstOrDefault()?.Code ?? "";
        }

        if (_isAdmin)
        {
            _roles = await sp.GetRequiredService<WebMemberService>().GetRoleOptionsAsync(GuildId);
        }
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

    protected override async Task OnParametersSetAsync()
    {
        if (State == AccessState.Ready && _supply is null && !string.IsNullOrWhiteSpace(_code))
        {
            await ReloadAsync();
        }
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
            _holders = await wallet.GetTopHoldersPageAsync(GuildId, _code, _holdersPage, PageSize);

            var (from, to) = ResolveRange(_movePreset);
            var sources = _moveSource is { } s ? new[] { s } : null;
            _movement = await wallet.GetMovementsPageAsync(
                GuildId, _code, _moveSearchBox, _moveSortKey, _moveDescending, _movePage, PageSize,
                sources: sources, from: from, to: to);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnCode(ChangeEventArgs e)
    {
        _code = (e.Value as string) ?? "";
        _holdersPage = 1;
        _movePage = 1;
        await ReloadAsync();
    }

    private async Task DebouncedMoveSearchAsync()
    {
        _moveSearchDebounce?.Cancel();
        var cts = _moveSearchDebounce = new CancellationTokenSource();
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
            _movePage = 1;
            await ReloadAsync();
        }
    }

    private async Task OnMoveRange(ChangeEventArgs e)
    {
        _movePreset = (e.Value as string) ?? "";
        _movePage = 1;
        await ReloadAsync();
    }

    private async Task OnMoveSource(ChangeEventArgs e)
    {
        var raw = e.Value as string;
        _moveSource = string.IsNullOrEmpty(raw) || !Enum.TryParse<CurrencyLedgerSource>(raw, out var v) ? null : v;
        _movePage = 1;
        await ReloadAsync();
    }

    private async Task SetMoveSort(string column)
    {
        _moveDescending = _moveSortKey == column ? !_moveDescending : true;
        _moveSortKey = column;
        _movePage = 1;
        await ReloadAsync();
    }

    private async Task HoldersPrev()
    {
        if (_holdersPage > 1)
        {
            _holdersPage--;
            await ReloadAsync();
        }
    }

    private async Task HoldersNext()
    {
        if (_holdersPage < HoldersTotalPages)
        {
            _holdersPage++;
            await ReloadAsync();
        }
    }

    private async Task MovePrev()
    {
        if (_movePage > 1)
        {
            _movePage--;
            await ReloadAsync();
        }
    }

    private async Task MoveNext()
    {
        if (_movePage < MoveTotalPages)
        {
            _movePage++;
            await ReloadAsync();
        }
    }

    private string MoveInd(string column) => _moveSortKey == column ? (_moveDescending ? "▼" : "▲") : "";

    private static string Medal(int rank) => rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => rank.ToString() };

    public void Dispose()
    {
        _moveSearchDebounce?.Cancel();
        _moveSearchDebounce?.Dispose();
    }
}
