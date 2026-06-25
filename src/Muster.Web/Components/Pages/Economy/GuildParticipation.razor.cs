using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence.Queries;
using Muster.Web.Components.Shared;

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
    private bool _canManage;   // economy manager (admin implied) — sees all tabs; auditors get the Ledger only

    private SeasonInfo? Current => _home?.Current?.Season;
    private SeasonInfo? Previous => _home?.Previous?.Season;
    private bool HasSeasons => _home is { Seasons.Count: > 0 };

    private string ActiveTab => !_canManage
        ? "ledger"
        : SeasonId is { } s
            ? s.ToString()
            : Section?.ToLowerInvariant() switch
            {
                "ranking" => "ranking",
                "ledger" => "ledger",
                _ => "overview",
            };

    /// <summary>Tab bar for the participation sections — auditors see only the Ledger; managers get Overview, the
    /// current + previous season, Ranking and the full Ledger.</summary>
    private IReadOnlyList<TabBar.TabItem> TabItems()
    {
        var ledger = new TabBar.TabItem("ledger", "Ledger", "receipt_long", $"/guilds/{GuildId}/participation/ledger");
        if (!_canManage)
        {
            return [ledger];
        }

        var items = new List<TabBar.TabItem>
        {
            new("overview", "Overview", "dashboard", $"/guilds/{GuildId}/participation"),
        };
        if (Current is { } c)
        {
            items.Add(new(c.Id.ToString(), c.Name, "local_fire_department", $"/guilds/{GuildId}/participation/season/{c.Id}"));
        }
        if (Previous is { } p)
        {
            items.Add(new(p.Id.ToString(), p.Name, "history", $"/guilds/{GuildId}/participation/season/{p.Id}"));
        }
        items.Add(new("ranking", "Ranking", "leaderboard", $"/guilds/{GuildId}/participation/ranking"));
        items.Add(ledger);
        return items;
    }

    protected override async Task LoadAsync()
    {
        _canManage = await Auth.IsEconomyManagerAsync(GuildId, UserId);
        await using var scope = Scopes.CreateAsyncScope();
        var points = scope.ServiceProvider.GetRequiredService<PointsReadService>();
        _supply = await points.GetSupplyAsync(GuildId);
        _home = await points.GetEngagementAsync(GuildId);
    }
}
