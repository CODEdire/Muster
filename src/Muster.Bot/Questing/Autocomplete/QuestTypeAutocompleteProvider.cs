using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Questing.Autocomplete;

/// <summary>Suggests the guild's admin-managed quest types (by name) — value is the quest-type id.</summary>
public class QuestTypeAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(QuestTypeAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var types = await db.QuestTypes
            .Where(t => t.GuildId == guildId && t.Name.Contains(input))
            .OrderBy(t => t.Sort).ThenBy(t => t.Name)
            .Take(25)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync();

        return types.Select(t => new ApplicationCommandOptionChoiceProperties(t.Name, t.Id.ToString()));
    }
}
