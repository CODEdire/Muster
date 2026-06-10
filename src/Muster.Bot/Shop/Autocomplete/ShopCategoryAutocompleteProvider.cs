using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Shop.Autocomplete;

/// <summary>Suggests the guild's shop categories (by name) for category parameters — value is the category id.</summary>
public class ShopCategoryAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(ShopCategoryAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var categories = await db.ShopCategories
            .Where(c => c.GuildId == guildId && c.Name.Contains(input))
            .OrderBy(c => c.Sort).ThenBy(c => c.Name)
            .Take(25)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync();

        return categories.Select(c => new ApplicationCommandOptionChoiceProperties(c.Name, c.Id.ToString()));
    }
}
