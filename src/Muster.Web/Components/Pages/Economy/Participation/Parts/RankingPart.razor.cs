using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence.Queries;

namespace Muster.Web.Components.Pages.Economy.Participation.Parts;

public partial class RankingPart : ParticipationPart
{
    private IReadOnlyList<SeasonInfo> _seasons = [];
    private string _scope = "";   // "all" for the all-time hall of fame, else a season id.
    private bool _resolved;
    private PagedResult<LeaderboardRow>? _holders;
    private int _page = 1;
    private int _pageSize = 25;

    private int TotalPages => _holders?.TotalPages ?? 1;

    protected override async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();

            if (!_resolved)
            {
                _seasons = await points.GetSeasonsAsync(GuildId);
                _scope = (_seasons.FirstOrDefault(s => s.IsActive)?.Id ?? _seasons.FirstOrDefault()?.Id)?.ToString() ?? "all";
                _resolved = true;
            }

            _holders = _scope == "all"
                ? await points.GetAllTimeRankingPageAsync(GuildId, _page, _pageSize)
                : await points.GetTopHoldersPageAsync(GuildId, _page, _pageSize, season: Guid.TryParse(_scope, out var sid) ? sid : null);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnScope(ChangeEventArgs e)
    {
        _scope = (e.Value as string) ?? "all";
        _page = 1;
        await ReloadAsync();
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
}
