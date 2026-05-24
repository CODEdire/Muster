using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Autocomplete;

/// <summary>Suggests the guild's currency codes as a user types a currency parameter.</summary>
public class CurrencyAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var currencies = await scope.ServiceProvider.GetRequiredService<CurrencyAdminService>().ListAsync(guildId);

        return currencies
            .Where(c => c.Code.Contains(input, StringComparison.OrdinalIgnoreCase)
                || c.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(c => new ApplicationCommandOptionChoiceProperties($"{c.Code} — {c.Name}", c.Code));
    }
}
