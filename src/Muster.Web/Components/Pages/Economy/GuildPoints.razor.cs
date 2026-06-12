using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Membership;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using Muster.Web.Components.Shared;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildPoints : IDisposable
{
    // Read-only points view — EconomyManager + Auditor. Admin passes implicitly.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    private const int PageSize = 25;
    private const int SearchDebounceMs = 350;

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

    private int HoldersTotalPages => _holders?.TotalPages ?? 1;
    private int MoveTotalPages => _movement?.TotalPages ?? 1;

    protected override async Task LoadAsync()
    {
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();
            _supply = await points.GetSupplyAsync(GuildId);
            _holders = await points.GetTopHoldersPageAsync(GuildId, _holdersPage, PageSize);

            var (from, to) = ResolveRange(_movePreset);
            var sources = _moveSource is { } s ? new[] { s } : null;
            _movement = await points.GetMovementsPageAsync(
                GuildId, _moveSearchBox, _moveSortKey, _moveDescending, _movePage, PageSize,
                sources: sources, from: from, to: to);
        }
        finally
        {
            _loading = false;
        }
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
