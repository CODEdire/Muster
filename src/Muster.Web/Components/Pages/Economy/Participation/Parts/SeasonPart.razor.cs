using System.Globalization;
using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services.Web;

namespace Muster.Web.Components.Pages.Economy.Participation.Parts;

public partial class SeasonPart : ParticipationPart
{
    [Parameter] public Guid SeasonId { get; set; }
    [Parameter, EditorRequired] public ParticipationHome? Home { get; set; }

    protected override object? ReloadKey => SeasonId;

    private SeasonEngagement? _seg;
    private IReadOnlyList<WeeklyPoint> _weekly = [];
    private IReadOnlyList<SourceEarned> _sources = [];
    private IReadOnlyList<LeaderboardRow> _podium = [];

    // KPI sparklines (cross-season trend for context, Flow-style).
    private string _awardedSpark = "", _partSpark = "", _newSpark = "", _retSpark = "";
    private IReadOnlyList<long> _cumWeekly = [];

    private IReadOnlyList<SeasonEngagement> Seasons => Home?.Seasons ?? [];
    private long WeeklyMax => _weekly.Count == 0 ? 1 : Math.Max(1, _weekly.Max(w => w.Total));
    private long CumMax => _cumWeekly.Count == 0 ? 1 : Math.Max(1, _cumWeekly.Max());
    private long SourceMax => _sources.Count == 0 ? 1 : Math.Max(1, _sources.Max(s => s.Total));
    private int Churn => _seg is { } s ? Math.Max(0, s.PriorEarners - s.ReturningEarners) : 0;

    // Earner-mix donut (returning + new = earners), r = 48, circumference ≈ 301.593.
    private double Seg(long part, long total) => total > 0 ? (double)part / total * 301.593 : 0;
    private string Dash(long part, long total) => $"{Seg(part, total).ToString("0.##", CultureInfo.InvariantCulture)} 301.593";
    private static string Off(double v) => (-v).ToString("0.##", CultureInfo.InvariantCulture);

    protected override async Task ReloadAsync()
    {
        _seg = Seasons.FirstOrDefault(s => s.Season.Id == SeasonId);

        await using var scope = Scopes.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();
        _weekly = await points.GetSeasonWeeklyAsync(GuildId, SeasonId);
        _sources = await points.GetSourcesAsync(GuildId, SeasonId);
        _podium = await points.GetTopEarnersAsync(GuildId, SeasonId, 5);

        var cum = new List<long>(_weekly.Count);
        long run = 0;
        foreach (var w in _weekly)
        {
            run += w.Total;
            cum.Add(run);
        }

        _cumWeekly = cum;
        _awardedSpark = PointsOverviewPart.Spark(Seasons.Select(s => (double)s.Awarded));
        _partSpark = PointsOverviewPart.Spark(Seasons.Select(s => (double)s.ParticipationPct));
        _newSpark = PointsOverviewPart.Spark(Seasons.Select(s => (double)s.NewEarners));
        _retSpark = PointsOverviewPart.Spark(Seasons.Select(s => (double)s.ReturningEarners));
    }

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
