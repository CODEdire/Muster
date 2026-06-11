using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Contracts;
using Muster.Persistence;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Shop.Autocomplete;

/// <summary>
/// Suggests the caller's own sold-out / expired listings (by name) for the <c>/shop listing relist</c> parameter —
/// only a dead listing is relisted into a fresh copy (an Active one tops up via <c>add-stock</c>). Scoped to the actor.
/// </summary>
public class ShopRelistableListingAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(ShopRelistableListingAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;
        var sellerId = context.Interaction.User.Id;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var listings = await db.ShopListings
            .Where(l => l.GuildId == guildId && l.SellerId == sellerId
                && (l.Status == ShopListingStatus.SoldOut || l.Status == ShopListingStatus.Expired)
                && l.Name.Contains(input))
            .OrderByDescending(l => l.StatusChangedAt)
            .Take(25)
            .Select(l => new { l.Id, l.Name, l.Price })
            .ToListAsync();

        return listings.Select(l => new ApplicationCommandOptionChoiceProperties($"{l.Name} ({l.Price})", l.Id.ToString()));
    }
}
