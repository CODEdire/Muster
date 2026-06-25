using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy.Treasury.Parts;

public partial class LedgerPart : TreasuryPart, IDisposable
{
    private const int PageSize = 25;
    private const int SearchDebounceMs = 350;

    private CurrencySupply? _supply;

    // Supply-trend mini panel (90d circulating-supply sparkline + net delta).
    private string _trendSpark = "";
    private long _trendNet;
    private int _trendPct;

    // Journal state.
    private PagedResult<JournalRow>? _journal;
    private (long In, long Out) _totals;
    private int _page = 1;
    private string? _searchBox;
    private string _preset = "";
    private CurrencyLedgerSource? _source;
    private int? _sign;
    private string? _account;
    private string _sortKey = "when";
    private bool _descending = true;
    private long? _expandedId;
    private CancellationTokenSource? _searchDebounce;

    private int TotalPages => _journal?.TotalPages ?? 1;
    private bool Grouped => _sortKey == "when";
    private bool Reconciles => _supply is { } s && s.Minted == s.Circulating + s.Escrow + s.Removed;
    private int Pct(long part) => _supply is { Minted: > 0 } s ? (int)(part * 100 / s.Minted) : 0;

    // Donut geometry for the supply-composition ring (r = 48, circumference ≈ 301.593).
    private double Seg(long part) => _supply is { Minted: > 0 } s ? (double)part / s.Minted * 301.593 : 0;
    private string Dash(long part) => $"{Seg(part).ToString("0.##", CultureInfo.InvariantCulture)} 301.593";
    private static string Off(double v) => (-v).ToString("0.##", CultureInfo.InvariantCulture);

    protected override async Task ReloadAsync()
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return;
        }

        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
            _supply = await wallet.GetSupplyAsync(GuildId, Code);
            BuildTrend(await wallet.GetSupplySeriesAsync(GuildId, Code, DateTimeOffset.UtcNow.AddDays(-90), DateTimeOffset.UtcNow));

            var (from, to) = ResolveRange(_preset);
            var sources = _source is { } s ? new[] { s } : null;
            _journal = await wallet.GetJournalAsync(GuildId, Code, _searchBox, _page, PageSize, sources, from, to, _sign, _account, _sortKey, _descending);
            _totals = await wallet.GetJournalTotalsAsync(GuildId, Code, _searchBox, sources, from, to, _sign, _account);
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
            StateHasChanged();
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

    private record Line(string? Header, long HeaderDebit, long HeaderCredit, JournalRow Row);

    /// <summary>Journal rows for rendering. When date-sorted, a day header (with that day's debit/credit subtotals)
    /// precedes each new day; otherwise a flat list.</summary>
    private IReadOnlyList<Line> Lines()
    {
        if (_journal is null)
        {
            return [];
        }

        var byDay = Grouped
            ? _journal.Items.GroupBy(r => DateOnly.FromDateTime(r.OccurredAt.UtcDateTime))
                .ToDictionary(g => g.Key, g => (Debit: g.Sum(x => x.Amount < 0 ? -x.Amount : 0L), Credit: g.Sum(x => x.Amount > 0 ? x.Amount : 0L)))
            : null;

        var list = new List<Line>(_journal.Items.Count);
        DateOnly? last = null;
        foreach (var r in _journal.Items)
        {
            if (Grouped)
            {
                var d = DateOnly.FromDateTime(r.OccurredAt.UtcDateTime);
                if (last != d)
                {
                    last = d;
                    var t = byDay![d];
                    list.Add(new Line(DateLabel(d), t.Debit, t.Credit, r));
                    continue;
                }
            }

            list.Add(new Line(null, 0, 0, r));
        }

        return list;
    }

    private async Task SetSort(string column)
    {
        _descending = _sortKey == column ? !_descending : true;
        _sortKey = column;
        _page = 1;
        await ReloadAsync();
    }

    private string Ind(string column) => _sortKey == column ? (_descending ? "▼" : "▲") : "";

    private void ToggleDetail(long id) => _expandedId = _expandedId == id ? null : id;

    private async Task ExportCsvAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
        var (from, to) = ResolveRange(_preset);
        var sources = _source is { } s ? new[] { s } : null;
        var rows = await wallet.GetJournalForExportAsync(GuildId, Code, _searchBox, sources, from, to, _sign, _account);

        var sb = new StringBuilder();
        sb.AppendLine("When (UTC),Account,Counter-account,Source,Debit,Credit,Reference,Reason");
        foreach (var r in rows)
        {
            sb.Append(r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(r.AccountName)).Append(',').Append(Csv(r.CounterName)).Append(',').Append(Csv(SourceLabel(r.Source))).Append(',')
              .Append(r.Amount < 0 ? (-r.Amount).ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(r.Amount > 0 ? r.Amount.ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(Csv(r.Reference ?? "")).Append(',').AppendLine(Csv(r.Reason));
        }

        await JS.InvokeVoidAsync("musterDownload", $"ledger-{Code}-{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd}.csv", sb.ToString(), "text/csv;charset=utf-8");
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

    /// <summary>Scale a 90-day circulating-supply series into a 100×30 sparkline plus the net delta / % over the window.</summary>
    private void BuildTrend(IReadOnlyList<BalancePoint> series)
    {
        _trendSpark = "";
        _trendNet = 0;
        _trendPct = 0;
        if (series.Count < 2)
        {
            return;
        }

        _trendNet = series[^1].Balance - series[0].Balance;
        if (series[0].Balance != 0)
        {
            _trendPct = (int)((series[^1].Balance - series[0].Balance) * 100 / Math.Abs(series[0].Balance));
        }

        long min = series.Min(p => p.Balance), max = series.Max(p => p.Balance);
        var span = Math.Max(1, max - min);
        var sb = new StringBuilder();
        for (var i = 0; i < series.Count; i++)
        {
            var x = i * (100.0 / (series.Count - 1));
            var y = 28 - (series[i].Balance - min) / (double)span * 26;
            sb.Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
              .Append(y.ToString("0.#", CultureInfo.InvariantCulture)).Append(' ');
        }

        _trendSpark = sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }
}
