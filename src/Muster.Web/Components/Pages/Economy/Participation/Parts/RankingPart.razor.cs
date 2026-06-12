using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence.Queries;

namespace Muster.Web.Components.Pages.Economy.Participation.Parts;

public partial class RankingPart : ParticipationPart
{
    private const int PageSize = 25;

    private IReadOnlyList<SeasonInfo> _seasons = [];
    private Guid? _season;
    private bool _resolved;
    private PagedResult<LeaderboardRow>? _holders;
    private int _page = 1;

    private int TotalPages => _holders?.TotalPages ?? 1;

    protected override async Task ReloadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();

        if (!_resolved)
        {
            _seasons = await points.GetSeasonsAsync(GuildId);
            _season = _seasons.FirstOrDefault(s => s.IsActive)?.Id ?? _seasons.FirstOrDefault()?.Id;
            _resolved = true;
        }

        _holders = await points.GetTopHoldersPageAsync(GuildId, _page, PageSize, season: _season);
    }

    private async Task OnSeason(ChangeEventArgs e)
    {
        _season = Guid.TryParse(e.Value as string, out var id) ? id : null;
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
