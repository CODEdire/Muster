using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildFlow
{
    // Read-only treasury view — EconomyManager + Auditor. Admin passes implicitly.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    [SupplyParameterFromQuery(Name = "cur")] private string? Cur { get; set; }

    private IReadOnlyList<CurrencyInfo> _currencies = [];
    private string _code = "";
    private bool _loading;

    private FlowView? _flow;
    private IReadOnlyList<FlowMonth> _series = [];

    // Pre-built SVG geometry for the over-time visuals.
    private string _mintSpark = "", _burnSpark = "", _netSpark = "";
    private string _inflationLine = "";
    private double _inflationZeroY;
    private long _flowBarMax = 1;

    private static readonly string[] MonthNames =
        ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Largest single source magnitude on either side — for scaling the diverging bars.</summary>
    private long FlowMax
    {
        get
        {
            if (_flow is null)
            {
                return 1;
            }

            long m = 1;
            foreach (var f in _flow.Faucets)
            {
                m = Math.Max(m, f.Total);
            }

            foreach (var s in _flow.Sinks)
            {
                m = Math.Max(m, s.Total);
            }

            return m;
        }
    }

    /// <summary>Inflation needle position (0–100%) across a -10%..+10% band.</summary>
    private int InflationPos => _flow is null ? 50 : (int)Math.Clamp(50 + _flow.InflationPct * 5, 0, 100);

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
        _currencies = await wallet.GetCurrenciesAsync(GuildId);
        if (!string.IsNullOrWhiteSpace(Cur) && _currencies.Any(c => c.Code == Cur))
        {
            _code = Cur!;
        }
        else if (string.IsNullOrWhiteSpace(_code) || _currencies.All(c => c.Code != _code))
        {
            _code = _currencies.FirstOrDefault(c => c.Primary)?.Code ?? _currencies.FirstOrDefault()?.Code ?? "";
        }

        await ReloadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State == AccessState.Ready && _flow is null && !string.IsNullOrWhiteSpace(_code))
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
            _flow = await wallet.GetFlowAsync(GuildId, _code, to.AddDays(-30), to);
            _series = await wallet.GetFlowSeriesAsync(GuildId, _code, to.AddMonths(-6), to);
            BuildVisuals();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Build the KPI sparklines, the bar-chart scale and the inflation-over-time line from the monthly series.</summary>
    private void BuildVisuals()
    {
        _mintSpark = Spark(_series.Select(s => s.Minted).ToList());
        _burnSpark = Spark(_series.Select(s => s.Burned).ToList());
        _netSpark = Spark(_series.Select(s => s.Net).ToList());
        _flowBarMax = _series.Count == 0 ? 1 : Math.Max(1, _series.Max(s => Math.Max(s.Minted, s.Burned)));

        // Monthly inflation = that month's net as a share of the supply at the month's start. Cumulate from the
        // window's opening supply (current circulating minus the window's total net).
        _inflationLine = "";
        if (_flow is not null && _series.Count >= 2)
        {
            double cum = _flow.Circulating - _series.Sum(s => s.Net);
            var infl = new List<double>(_series.Count);
            foreach (var m in _series)
            {
                infl.Add(cum > 0 ? m.Net * 100.0 / cum : 0);
                cum += m.Net;
            }

            _inflationLine = PolyD(infl, 600, 70, out _inflationZeroY);
        }
    }

    /// <summary>Sparkline polyline over a 100×26 box (flat line when all equal).</summary>
    private static string Spark(IReadOnlyList<long> vals)
    {
        if (vals.Count < 2)
        {
            return "";
        }

        long min = vals.Min(), max = vals.Max();
        var range = Math.Max(1, max - min);
        var sb = new StringBuilder();
        for (var i = 0; i < vals.Count; i++)
        {
            var x = i * (100.0 / (vals.Count - 1));
            var y = 24 - (vals[i] - min) / (double)range * 22;
            sb.Append(Fmt(x)).Append(',').Append(Fmt(y)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Polyline for double values over a w×h box, also reporting where y=0 maps (the inflation zero line).</summary>
    private static string PolyD(IReadOnlyList<double> vals, double w, double h, out double zeroY)
    {
        double min = Math.Min(0, vals.Min()), max = Math.Max(0, vals.Max());
        var range = Math.Max(0.001, max - min);
        zeroY = h - (0 - min) / range * h;
        var sb = new StringBuilder();
        for (var i = 0; i < vals.Count; i++)
        {
            var x = i * (w / (vals.Count - 1));
            var y = h - (vals[i] - min) / range * h;
            sb.Append(Fmt(x)).Append(',').Append(Fmt(y)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    private static string InflationRead(double pct) => pct switch
    {
        < 0 => "deflating — burns outpace mints",
        <= 5 => "stable — healthy band",
        _ => "hot — mint outpacing burn",
    };
}
