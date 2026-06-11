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
/// Suggests any in-stock, active listing in the guild (by name) for the <c>/shop buy</c> item parameter — unlike
/// <see cref="ShopListingAutocompleteProvider"/> (the seller's own listings, for management), a buyer can pick
/// anyone's item. Featured items float to the top, then newest.
/// </summary>
public class ShopBuyListingAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(ShopBuyListingAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var listings = await db.ShopListings
            .Where(l => l.GuildId == guildId && l.Status == ShopListingStatus.Active && l.Quantity > 0 && l.Name.Contains(input))
            .OrderByDescending(l => l.FeaturedUntil != null && l.FeaturedUntil > now)
            .ThenByDescending(l => l.CreatedAt)
            .Take(25)
            .Select(l => new { l.Id, l.Name, l.Price })
            .ToListAsync();

        return listings.Select(l => new ApplicationCommandOptionChoiceProperties($"{l.Name} ({l.Price})", l.Id.ToString()));
    }
}
