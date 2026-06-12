using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildTreasury
{
    // Read-only treasury view — EconomyManager + Auditor. Admin passes implicitly.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _code = "";
    private bool _loading;

    private CurrencySupply? _supply;
    private IReadOnlyList<LeaderboardRow> _topHolders = [];
    private IReadOnlyList<MovementRow> _recent = [];
    private IReadOnlyList<BalancePoint> _series = [];

    private CurrencyInfo? Selected => _currencies.FirstOrDefault(c => c.Code == _code);

    // Supply breakdown bar (of all-time minted): circulating / escrow / burned.
    private int CircPct => _supply is { Minted: > 0 } s ? (int)(s.Circulating * 100 / s.Minted) : 100;
    private int EscrowPct => _supply is { Minted: > 0 } s ? (int)(s.Escrow * 100 / s.Minted) : 0;
    private int BurnedPct => _supply is { Minted: > 0 } s ? (int)(s.Removed * 100 / s.Minted) : 0;

    private long NetSupply => _series.Count < 2 ? 0 : _series[^1].Balance - _series[0].Balance;
    private int TrendPct
    {
        get
        {
            if (_series.Count < 2 || _series[0].Balance == 0)
            {
                return 0;
            }

            return (int)((_series[^1].Balance - _series[0].Balance) * 100 / Math.Abs(_series[0].Balance));
        }
    }

    // Candle chart (circulating supply OHLC per month) + close line + moving average + regression trend.
    public record CandleVm(double X, double BodyW, double BodyTop, double BodyH, double WickTop, double WickH, bool Up, string Label);

    private const double CTop = 12;
    private const double CBot = 172;
    private const double CWidth = 600;
    private IReadOnlyList<CandleVm> _candles = [];
    private string _closeLine = "";
    private string _maLine = "";
    private (double X1, double Y1, double X2, double Y2)? _trend;

    // Per-month net supply change for the net-supply mini bars (green up / red down around zero).
    private IReadOnlyList<long> _monthlyNet = [];
    private long NetBarMax => _monthlyNet.Count == 0 ? 1 : Math.Max(1, _monthlyNet.Max(v => Math.Abs(v)));

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
        _currencies = await wallet.GetCurrenciesAsync(GuildId);
        if (string.IsNullOrWhiteSpace(_code) || _currencies.All(c => c.Code != _code))
        {
            _code = _currencies.FirstOrDefault(c => c.Primary)?.Code ?? _currencies.FirstOrDefault()?.Code ?? "";
        }

        await ReloadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State == AccessState.Ready && _supply is null && !string.IsNullOrWhiteSpace(_code))
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
            var to = DateTimeOffset.UtcNow;
            var from = to.AddDays(-180);

            _supply = await wallet.GetSupplyAsync(GuildId, _code);
            _series = await wallet.GetSupplySeriesAsync(GuildId, _code, from, to);
            _topHolders = (await wallet.GetTopHoldersPageAsync(GuildId, _code, 1, 5)).Items;
            _recent = (await wallet.GetMovementsPageAsync(GuildId, _code, null, "when", true, 1, 5)).Items;
            ComputeCandles();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Build per-month OHLC candles of circulating supply, a close line, a trailing 3-month moving average,
    /// and a regression trend — all pre-scaled to the 600×180 viewbox.</summary>
    private void ComputeCandles()
    {
        _candles = [];
        _closeLine = "";
        _maLine = "";
        _trend = null;
        _monthlyNet = [];
        if (_series.Count == 0)
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
        var maPts = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var o = ohlc[i];
            var x = i * gw + gw / 2;
            var bodyTop = Y(Math.Max(o.Open, o.Close));
            var bodyBottom = Y(Math.Min(o.Open, o.Close));
            vms.Add(new CandleVm(x, bw, bodyTop, Math.Max(1, bodyBottom - bodyTop), Y(o.High), Math.Max(1, Y(o.Low) - Y(o.High)), o.Close >= o.Open, MonthNames[o.Month]));
            closePts.Append(Fmt(x)).Append(',').Append(Fmt(Y(o.Close))).Append(' ');

            // trailing 3-month moving average of closes
            var lo = Math.Max(0, i - 2);
            double maVal = 0;
            for (var j = lo; j <= i; j++)
            {
                maVal += ohlc[j].Close;
            }

            maVal /= (i - lo + 1);
            maPts.Append(Fmt(x)).Append(',').Append(Fmt(Y(maVal))).Append(' ');
        }

        _candles = vms;
        _closeLine = closePts.ToString().TrimEnd();
        _maLine = maPts.ToString().TrimEnd();
        _monthlyNet = ohlc.Select(o => o.Close - o.Open).ToList();

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

    private static readonly string[] MonthNames =
        ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string AccountLabel(string account) => account switch
    {
        "escrow" => "escrow",
        "burn" => "burn",
        _ => "member",
    };
}
