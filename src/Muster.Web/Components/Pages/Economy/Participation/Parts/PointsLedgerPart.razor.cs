using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence.Queries;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy.Participation.Parts;

public partial class PointsLedgerPart : ParticipationPart, IDisposable
{
    private const int SearchDebounceMs = 350;

    private IReadOnlyList<SeasonInfo> _seasons = [];
    private PagedResult<PointsLedgerRow>? _ledger;
    private (long In, long Out) _totals;
    private int _page = 1;
    private int _pageSize = 25;
    private string? _searchBox;
    private string _preset = "";
    private CurrencyLedgerSource? _source;
    private Guid? _season;
    private string _sortKey = "when";
    private bool _descending = true;
    private CancellationTokenSource? _searchDebounce;

    private int TotalPages => _ledger?.TotalPages ?? 1;
    private bool Grouped => _sortKey == "when";

    protected override async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();
            if (_seasons.Count == 0)
            {
                _seasons = await points.GetSeasonsAsync(GuildId);
            }

            var (from, to) = ResolveRange(_preset);
            var sources = _source is { } s ? new[] { s } : null;
            _ledger = await points.GetLedgerPageAsync(GuildId, _searchBox, _sortKey, _descending, _page, _pageSize, sources, from, to, _season);
            _totals = await points.GetLedgerTotalsAsync(GuildId, _searchBox, sources, from, to, _season);
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

    private async Task OnSeason(ChangeEventArgs e)
    {
        _season = Guid.TryParse(e.Value as string, out var id) ? id : null;
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
            StateHasChanged();
        }
    }

    private async Task OnSize(ChangeEventArgs e)
    {
        _pageSize = int.TryParse(e.Value as string, out var v) ? Math.Clamp(v, 10, 100) : 25;
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

    private async Task SetSort(string column)
    {
        _descending = _sortKey == column ? !_descending : true;
        _sortKey = column;
        _page = 1;
        await ReloadAsync();
    }

    private string Ind(string column) => _sortKey == column ? (_descending ? "▼" : "▲") : "";

    private record Line(string? Header, long HeaderIn, PointsLedgerRow Row);

    /// <summary>Ledger rows for rendering. When date-sorted, a day header (with that day's earned subtotal) precedes
    /// each new day; otherwise a flat list.</summary>
    private IReadOnlyList<Line> Lines()
    {
        if (_ledger is null)
        {
            return [];
        }

        var byDay = Grouped
            ? _ledger.Items.GroupBy(r => DateOnly.FromDateTime(r.OccurredAt.UtcDateTime))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount > 0 ? x.Amount : 0L))
            : null;

        var list = new List<Line>(_ledger.Items.Count);
        DateOnly? last = null;
        foreach (var r in _ledger.Items)
        {
            if (Grouped)
            {
                var d = DateOnly.FromDateTime(r.OccurredAt.UtcDateTime);
                if (last != d)
                {
                    last = d;
                    list.Add(new Line(DateLabel(d), byDay![d], r));
                    continue;
                }
            }

            list.Add(new Line(null, 0, r));
        }

        return list;
    }

    private async Task ExportCsvAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();
        var (from, to) = ResolveRange(_preset);
        var sources = _source is { } s ? new[] { s } : null;
        var rows = await points.GetLedgerForExportAsync(GuildId, _searchBox, _sortKey, _descending, sources, from, to, _season);

        var sb = new StringBuilder();
        sb.AppendLine("When (UTC),Member,Amount,Source,Season,Reason");
        foreach (var r in rows)
        {
            sb.Append(r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(r.DisplayName)).Append(',')
              .Append(r.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(SourceLabel(r.Source))).Append(',').Append(Csv(r.SeasonName)).Append(',')
              .AppendLine(Csv(r.Reason));
        }

        await JS.InvokeVoidAsync("musterDownload", $"points-ledger-{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd}.csv", sb.ToString(), "text/csv;charset=utf-8");
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string DateLabel(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (d == today)
        {
            return "Today";
        }

        return d == today.AddDays(-1) ? "Yesterday" : d.ToString("ddd, d MMM yyyy");
    }

    public void Dispose()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }
}
