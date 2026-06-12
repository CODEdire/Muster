using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy;

public partial class Points : IDisposable
{
    private const int SearchDebounceMs = 350;
    private string? _searchBox;
    private string _preset = "";
    private CurrencyLedgerSource? _source;
    private CancellationTokenSource? _searchDebounce;
    private string _sortKey = "when";
    private bool _descending = true;
    private int _page = 1;
    private int _size = 25;
    private bool _loading;

    private PointsSnapshot _snapshot = new(0, false, new Muster.Infrastructure.Services.Tracking.MemberVoiceStats(0, 0, 0));
    private PagedResult<MemberLedgerRow>? _history;

    private int TotalPages => _history?.TotalPages ?? 1;

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        _snapshot = await scope.ServiceProvider.GetRequiredService<PointsReadService>().GetSnapshotAsync(GuildId, UserId);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State == AccessState.Ready && _history is null)
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
            var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();

            var (from, to) = ResolveRange(_preset);
            var sources = _source is { } s ? new[] { s } : null;
            _history = await points.GetHistoryPageAsync(
                GuildId, UserId, _searchBox, _sortKey, _descending, _page, _size,
                sources: sources, from: from, to: to);
            _snapshot = await points.GetSnapshotAsync(GuildId, UserId);
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

    public void Dispose()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }

    private static string FormatDuration(int minutes)
    {
        var hours = minutes / 60;
        var mins = minutes % 60;
        return hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
    }
}
