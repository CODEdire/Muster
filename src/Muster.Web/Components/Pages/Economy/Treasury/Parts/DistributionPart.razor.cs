using System.Globalization;
using System.Text;
using Muster.Infrastructure.Services.Web;

namespace Muster.Web.Components.Pages.Economy.Treasury.Parts;

public partial class DistributionPart : TreasuryPart
{
    private DistributionView? _dist;
    private IReadOnlyList<LeaderboardRow> _topHolders = [];
    private DistributionSeries? _series;

    private string _holdersSpark = "", _top10Spark = "", _giniSpark = "", _medianSpark = "";
    private int _stackMax = 1;

    private int BracketMax => _dist is { Brackets.Count: > 0 } d ? Math.Max(1, d.Brackets.Max(b => b.Count)) : 1;
    private int Bracket5Max => _dist is { Brackets5.Count: > 0 } d ? Math.Max(1, d.Brackets5.Max(b => b.Count)) : 1;

    // Blue → amber ramp across the 10 wealth buckets (low to high) for the buckets-over-time chart.
    private static readonly string[] BucketColors =
        ["#185FA5", "#2E73B5", "#4488C5", "#5C97C0", "#7FA9A0", "#A8B574", "#CBB94B", "#E0AD33", "#EF9F27", "#BA7517"];

    private static readonly string[] MonthNames =
        ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Spark(IEnumerable<double> source)
    {
        var vals = source.ToList();
        if (vals.Count < 2)
        {
            return "";
        }

        var min = vals.Min();
        var range = Math.Max(0.001, vals.Max() - min);
        var sb = new StringBuilder();
        for (var i = 0; i < vals.Count; i++)
        {
            var x = i * (100.0 / (vals.Count - 1));
            var y = 24 - (vals[i] - min) / range * 22;
            sb.Append(Fmt(x)).Append(',').Append(Fmt(y)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    protected override async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var wallet = scope.ServiceProvider.GetRequiredService<WalletReadService>();
            _dist = await wallet.GetDistributionAsync(GuildId, Code);
            _topHolders = await wallet.GetTopHoldersLedgerAsync(GuildId, Code, 10);
            _series = await wallet.GetDistributionSeriesAsync(GuildId, Code, 6);
            BuildVisuals();
        }
        finally
        {
            _loading = false;
        }
    }

    private void BuildVisuals()
    {
        if (_series is not { Points.Count: > 0 } s)
        {
            _holdersSpark = _top10Spark = _giniSpark = _medianSpark = "";
            _stackMax = 1;
            return;
        }

        _holdersSpark = Spark(s.Points.Select(p => (double)p.Holders));
        _top10Spark = Spark(s.Points.Select(p => (double)p.Top10Pct));
        _giniSpark = Spark(s.Points.Select(p => p.Gini));
        _medianSpark = Spark(s.Points.Select(p => (double)p.Median));
        _stackMax = Math.Max(1, s.Points.Max(p => p.Holders));
    }

    private static string Concentration(int top10) => top10 switch
    {
        >= 60 => "high concentration",
        >= 40 => "moderate concentration",
        _ => "fairly spread",
    };
}
