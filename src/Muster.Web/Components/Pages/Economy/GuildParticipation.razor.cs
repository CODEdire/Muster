using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence.Queries;

namespace Muster.Web.Components.Pages.Economy;

public partial class GuildParticipation
{
    // Read-only engagement shell — EconomyManager + Auditor. Admin passes implicitly. Owns the header + tab bar and
    // resolves the current/previous seasons; each tab body is an embedded part that loads its own data.
    protected override GuildAccessTier RequiredAccess =>
        GuildAccessTier.EconomyManager | GuildAccessTier.Auditor;

    [Parameter] public string? Section { get; set; }
    [Parameter] public Guid? SeasonId { get; set; }

    private CurrencySupply? _supply;
    private ParticipationHome? _home;

    private SeasonInfo? Current => _home?.Current?.Season;
    private SeasonInfo? Previous => _home?.Previous?.Season;
    private bool HasSeasons => _home is { Seasons.Count: > 0 };

    private string ActiveTab => SeasonId is { } s
        ? s.ToString()
        : Section?.ToLowerInvariant() switch
        {
            "ranking" => "ranking",
            "ledger" => "ledger",
            _ => "overview",
        };

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();
        _supply = await points.GetSupplyAsync(GuildId);
        _home = await points.GetEngagementAsync(GuildId);
    }
}
