using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Musters.Autocomplete;

/// <summary>Suggests the guild's enabled muster templates by name for the <c>/muster post</c> template option.</summary>
public class MusterTemplateAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(MusterTemplateAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

        var templates = await db.MusterTemplates
            .Where(t => t.GuildId == guildId && t.Enabled && t.Name.Contains(input))
            .OrderBy(t => t.Name)
            .Take(25)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync();

        return templates.Select(t => new ApplicationCommandOptionChoiceProperties(t.Name, t.Id.ToString()));
    }
}
