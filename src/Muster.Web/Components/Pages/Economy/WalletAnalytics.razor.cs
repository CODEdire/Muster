using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Web;
using static Muster.Web.Components.Shared.LedgerMeta;

namespace Muster.Web.Components.Pages.Economy;

public partial class WalletAnalytics
{
    private static readonly string[] MonthNames =
        ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _sel = "";
    private string _displayName = "";
    private string? _avatarUrl;
    private string _period = "6mo";

    private CurrencyInfo? Selected => _currencies.FirstOrDefault(c => c.Code == _sel);
    private string SelUnit => Selected?.Code ?? "";

    /// <summary>A non-spendable score currency (e.g. POINTS) only accrues — no candles, no spending breakdown.</summary>
    private bool IsScore => Selected is { Spendable: false };

    private WalletKpis _kpis = new(0, 0, 0, 0, 0, 0);
    private int _heldOrders;
    private int _rank;
    private int _holders;
    private IReadOnlyList<BalancePoint> _series = [];
    private IReadOnlyList<MonthFlow> _months = [];
    private IReadOnlyList<SourceFlow> _earned = [];
    private IReadOnlyList<SourceFlow> _spent = [];
    private PointsSnapshot? _points;

    // Candle chart (spendable currencies): per-month OHLC of balance, a close line and a regression trend line,
    // all pre-scaled to the 600x180 viewbox so the markup just draws them.
    private IReadOnlyList<CandleVm> _candles = [];
    private string _closeLine = "";
    private (double X1, double Y1, double X2, double Y2)? _trend;

    private const double CTop = 12;
    private const double CBot = 172;
    private const double CWidth = 600;

    public record CandleVm(double X, double BodyW, double BodyTop, double BodyH, double WickTop, double WickH, bool Up, string Label);

    private long EarnedMax => _earned.Count == 0 ? 1 : Math.Max(1, _earned.Max(s => s.Earned));
    private long SpentMax => _spent.Count == 0 ? 1 : Math.Max(1, _spent.Max(s => s.Spent));
    private int NetPositiveMonths => _months.Count(m => m.Earned - m.Spent >= 0);

    private static readonly (string Value, string Label)[] Periods =
        [("30d", "Last 30 days"), ("90d", "Last 90 days"), ("6mo", "Last 6 months"), ("1y", "Last year")];

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        _currencies = await sp.GetRequiredService<ICurrencyReadService>().GetCurrenciesAsync(GuildId);
        var detail = await sp.GetRequiredService<WebMemberService>().GetAsync(GuildId, UserId, historyCount: 1);
        _displayName = detail.DisplayName;
        _avatarUrl = detail.AvatarUrl;
        _sel = _currencies.FirstOrDefault(c => c.Primary)?.Code
            ?? _currencies.FirstOrDefault(c => c.Spendable)?.Code
            ?? _currencies.FirstOrDefault()?.Code
            ?? "";

        await LoadDataAsync();
    }

    private (DateTimeOffset From, DateTimeOffset To) Window()
    {
        var to = DateTimeOffset.UtcNow;
        var from = _period switch
        {
            "30d" => to.AddDays(-30),
            "90d" => to.AddDays(-90),
            "1y" => to.AddDays(-365),
            _ => to.AddDays(-180),
        };
        return (from, to);
    }

    private async Task LoadDataAsync()
    {
        if (string.IsNullOrEmpty(_sel))
        {
            return;
        }

        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var wallet = sp.GetRequiredService<WalletReadService>();
        var (from, to) = Window();

        _kpis = await wallet.GetKpisAsync(GuildId, UserId, _sel, from, to);
        _heldOrders = _kpis.Held > 0 ? await wallet.GetHeldOrderCountAsync(GuildId, UserId, _sel) : 0;
        (_rank, _holders) = await wallet.GetWealthRankAsync(GuildId, UserId, _sel);
        _series = await wallet.GetBalanceSeriesAsync(GuildId, UserId, _sel, from, to);
        _months = await wallet.GetCashFlowAsync(GuildId, UserId, _sel, from, to);
        ComputeCandles();

        var breakdown = await wallet.GetSourceBreakdownAsync(GuildId, UserId, _sel, from, to);
        _earned = breakdown.Where(s => s.Earned > 0).OrderByDescending(s => s.Earned).ToList();
        _spent = breakdown.Where(s => s.Spent > 0).OrderByDescending(s => s.Spent).ToList();

        // Score currencies (POINTS) get a participation framing — season standing + voice, not money.
        _points = IsScore ? await sp.GetRequiredService<PointsReadService>().GetSnapshotAsync(GuildId, UserId) : null;
    }

    /// <summary>Voice minutes as a short "Xh Ym" (or "Ym") label.</summary>
    private static string Hm(int minutes) => minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";

    private async Task SelectCurrency(string code)
    {
        if (code == _sel)
        {
            return;
        }

        _sel = code;
        await LoadDataAsync();
    }

    private async Task OnPeriod(ChangeEventArgs e)
    {
        _period = (e.Value as string) ?? "6mo";
        await LoadDataAsync();
    }

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Balance-over-time polyline points over a 600×150 viewbox (empty when too few points).</summary>
    private string BalanceLine()
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
            double x = i * (600.0 / (n - 1));
            double y = 140 - (_series[i].Balance - min) / range * 130;
            sb.Append(Fmt(x)).Append(',').Append(Fmt(y)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Closed area path matching <see cref="BalanceLine"/> (line + baseline) for the soft fill.</summary>
    private string BalanceArea()
    {
        var line = BalanceLine();
        return string.IsNullOrEmpty(line) ? "" : $"M0,150 L{line.Replace(" ", " L")} L600,150 Z";
    }

    /// <summary>Build per-month OHLC candles + close line + regression trend from the daily balance series (spendable
    /// currencies only). Open chains from the prior month's close; high/low span the month's daily balances.</summary>
    private void ComputeCandles()
    {
        _candles = [];
        _closeLine = "";
        _trend = null;
        if (IsScore || _series.Count == 0)
        {
            return;
        }

        var months = _series
            .GroupBy(p => (p.Date.Year, p.Date.Month))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .ToList();
        if (months.Count == 0)
        {
            return;
        }

        var ohlc = new List<(int Month, long Open, long High, long Low, long Close)>();
        long prevClose = 0;
        var first = true;
        foreach (var g in months)
        {
            var balances = g.Select(x => x.Balance).ToList();
            var close = balances[^1];
            var open = first ? balances[0] : prevClose;
            ohlc.Add((g.Key.Month, open, Math.Max(open, balances.Max()), Math.Min(open, balances.Min()), close));
            prevClose = close;
            first = false;
        }

        long minV = ohlc.Min(o => o.Low);
        long maxV = ohlc.Max(o => o.High);
        double Y(double v) => CBot - (v - minV) / Math.Max(1, maxV - minV) * (CBot - CTop);

        var n = ohlc.Count;
        var gw = CWidth / n;
        var bw = Math.Min(26, gw * 0.5);
        var vms = new List<CandleVm>(n);
        var closePts = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var o = ohlc[i];
            var x = i * gw + gw / 2;
            var bodyTop = Y(Math.Max(o.Open, o.Close));
            var bodyBottom = Y(Math.Min(o.Open, o.Close));
            var wickTop = Y(o.High);
            var wickBottom = Y(o.Low);
            vms.Add(new CandleVm(x, bw, bodyTop, Math.Max(1, bodyBottom - bodyTop), wickTop, Math.Max(1, wickBottom - wickTop), o.Close >= o.Open, MonthNames[o.Month]));
            closePts.Append(Fmt(x)).Append(',').Append(Fmt(Y(o.Close))).Append(' ');
        }

        _candles = vms;
        _closeLine = closePts.ToString().TrimEnd();

        double sx = 0, sy = 0, sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++)
        {
            sx += i;
            sy += ohlc[i].Close;
            sxy += i * (double)ohlc[i].Close;
            sxx += (double)i * i;
        }

        var denom = n * sxx - sx * sx;
        var b = denom == 0 ? 0 : (n * sxy - sx * sy) / denom;
        var a = (sy - b * sx) / n;
        _trend = (vms[0].X, Y(a), vms[^1].X, Y(a + b * (n - 1)));
    }

    private string MonthLabel(MonthFlow m) => MonthNames[m.Month];
    private string PercentOfEarned(SourceFlow s) => _earned.Sum(x => x.Earned) is var t && t > 0 ? $"{s.Earned * 100 / t}%" : "0%";
}
