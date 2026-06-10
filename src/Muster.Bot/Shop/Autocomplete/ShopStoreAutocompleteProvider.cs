using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Shop.Autocomplete;

/// <summary>Suggests open stores (by name) for the <c>/shop browse</c> store filter — value is the store id.</summary>
public class ShopStoreAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(ShopStoreAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var stores = await db.ShopStores
            .Where(s => s.GuildId == guildId && !s.Closed && s.Name.Contains(input))
            .OrderBy(s => s.Name)
            .Take(25)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync();

        return stores.Select(s => new ApplicationCommandOptionChoiceProperties(s.Name, s.Id.ToString()));
    }
}
