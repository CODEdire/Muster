using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services.Web;

namespace Muster.Web.Components.Pages.Economy.Participation.Parts;

public partial class PointsOverviewPart : ParticipationPart
{
    [Parameter, EditorRequired] public ParticipationHome? Home { get; set; }

    private IReadOnlyList<SourceEarned> _sources = [];
    private IReadOnlyList<LeaderboardRow> _podium = [];

    // KPI sparklines (cross-season trend, Flow-style).
    private string _awardedSpark = "", _reachSpark = "", _partSpark = "", _retSpark = "";
    private IReadOnlyList<long> _reachCum = [];

    protected override object? ReloadKey => Home;

    private IReadOnlyList<SeasonEngagement> Seasons => Home?.Seasons ?? [];
    private long AwardedMax => Seasons.Count == 0 ? 1 : Math.Max(1, Seasons.Max(s => s.Awarded));
    private int EarnerMax => Seasons.Count == 0 ? 1 : Math.Max(1, Seasons.Max(s => s.Earners));
    private int ChurnMax => Seasons.Count == 0 ? 1 : Math.Max(1, Seasons.Max(s => Math.Max(0, s.PriorEarners - s.ReturningEarners)));
    private long ReachMax => _reachCum.Count == 0 ? 1 : Math.Max(1, _reachCum.Max());
    private long SourceMax => _sources.Count == 0 ? 1 : Math.Max(1, _sources.Max(s => s.Total));

    private static int Churn(SeasonEngagement s) => Math.Max(0, s.PriorEarners - s.ReturningEarners);

    protected override async Task ReloadAsync()
    {
        if (Home is null)
        {
            return;
        }

        var cum = new List<long>(Seasons.Count);
        long run = 0;
        foreach (var s in Seasons)
        {
            run += s.NewEarners;
            cum.Add(run);
        }

        _reachCum = cum;
        _awardedSpark = Spark(Seasons.Select(s => (double)s.Awarded));
        _reachSpark = Spark(cum.Select(v => (double)v));
        _partSpark = Spark(Seasons.Select(s => (double)s.ParticipationPct));
        _retSpark = Spark(Seasons.Select(s => (double)s.RetentionPct));

        await using var scope = Scopes.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();
        _sources = await points.GetSourcesAsync(GuildId, null);
        _podium = await points.GetTopEarnersAsync(GuildId, null, 5);
    }

    internal static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Sparkline polyline over a 100×26 box (flat when fewer than two points).</summary>
    internal static string Spark(IEnumerable<double> source)
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
}
