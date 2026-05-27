using Microsoft.Extensions.DependencyInjection;
using Muster.Domain;
using Muster.Infrastructure.Services.Currencies;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Autocomplete;

/// <summary>
/// Currency suggestions for a <b>quest reward</b> — same as <see cref="CurrencyAutocompleteProvider"/> but excludes
/// the seasonal <c>POINTS</c> currency, which isn't a valid quest reward (guild quests mint tier bonus points
/// automatically; player quests escrow a spendable currency). The general provider is still used for <c>/award</c>,
/// where awarding points is fine.
/// </summary>
public class QuestCurrencyAutocompleteProvider(IServiceScopeFactory scopeFactory)
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
        var currencies = await scope.ServiceProvider.GetRequiredService<ICurrencyAdminService>().ListAsync(guildId);

        return currencies
            .Where(c => !string.Equals(c.Code, CurrencyCodes.PointsCode, StringComparison.OrdinalIgnoreCase))
            .Where(c => c.Code.Contains(input, StringComparison.OrdinalIgnoreCase)
                || c.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(c => new ApplicationCommandOptionChoiceProperties($"{c.Code} — {c.Name}", c.Code));
    }
}
