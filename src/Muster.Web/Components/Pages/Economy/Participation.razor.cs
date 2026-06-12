using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence.Queries;

namespace Muster.Web.Components.Pages.Economy;

public partial class Participation
{
    // Read-only engagement view — EconomyManager + Auditor. Admin passes implicitly.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    [SupplyParameterFromQuery(Name = "season")] private string? SeasonRaw { get; set; }

    private Guid? _season;
    private Guid? _loaded = Guid.Empty;   // sentinel distinct from "no season" (null)
    private bool _ready;

    private CurrencySupply? _supply;
    private ParticipationHome? _home;

    // Overview scope (cross-season).
    private IReadOnlyList<SourceEarned> _ovSources = [];
    private IReadOnlyList<LeaderboardRow> _ovPodium = [];

    // Single-season scope.
    private SeasonEngagement? _seg;
    private IReadOnlyList<WeeklyPoint> _weekly = [];
    private IReadOnlyList<SourceEarned> _seasonSources = [];
    private IReadOnlyList<LeaderboardRow> _seasonPodium = [];

    private bool IsSeason => _season is not null;
    private SeasonInfo? Current => _home?.Current?.Season;
    private SeasonInfo? Previous => _home?.Previous?.Season;
    private string ActiveTab => _season?.ToString() ?? "overview";

    private long SourceMax(IReadOnlyList<SourceEarned> s) => s.Count == 0 ? 1 : Math.Max(1, s.Max(x => x.Total));
    private long WeeklyMax => _weekly.Count == 0 ? 1 : Math.Max(1, _weekly.Max(w => w.Total));
    private long SeasonBarMax => _home is { Seasons.Count: > 0 } h ? Math.Max(1, h.Seasons.Max(s => s.Awarded)) : 1;

    protected override async Task LoadAsync()
    {
        _season = Guid.TryParse(SeasonRaw, out var s) ? s : null;
        await ReloadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (State != AccessState.Ready)
        {
            return;
        }

        var next = Guid.TryParse(SeasonRaw, out var s) ? (Guid?)s : null;
        if (next != _loaded || !_ready)
        {
            _season = next;
            await ReloadAsync();
        }
    }

    private async Task ReloadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();

        _supply = await points.GetSupplyAsync(GuildId);
        _home = await points.GetEngagementAsync(GuildId);

        if (_season is { } sid)
        {
            _seg = _home.Seasons.FirstOrDefault(x => x.Season.Id == sid);
            _weekly = await points.GetSeasonWeeklyAsync(GuildId, sid);
            _seasonSources = await points.GetSourcesAsync(GuildId, sid);
            _seasonPodium = await points.GetTopEarnersAsync(GuildId, sid, 5);
        }
        else
        {
            _ovSources = await points.GetSourcesAsync(GuildId, null);
            _ovPodium = await points.GetTopEarnersAsync(GuildId, null, 5);
        }

        _loaded = _season;
        _ready = true;
    }
}
