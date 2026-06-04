using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Domain.Enums;
using Muster.Persistence;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Musters.Autocomplete;

/// <summary>
/// Suggests open musters by title/prompt for muster id parameters, so staff never type a GUID. Only open musters are
/// offered (the commands that take an id — close — act on open ones); <c>/muster</c> lifecycle commands are
/// manager-gated, so there's nothing viewer-sensitive to filter here.
/// </summary>
public class MusterAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(MusterAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

        var musters = await db.ReactionMusters
            .Where(m => m.GuildId == guildId
                && m.Status == MusterStatus.Open
                && ((m.Title != null && m.Title.Contains(input)) || m.Prompt.Contains(input)))
            .OrderByDescending(m => m.CreatedAt)
            .Take(25)
            .Select(m => new { m.Id, m.Title, m.Prompt })
            .ToListAsync();

        return musters.Select(m =>
        {
            var label = string.IsNullOrWhiteSpace(m.Title) ? m.Prompt : m.Title!;
            label = label.Length <= 100 ? label : label[..99] + "…";
            return new ApplicationCommandOptionChoiceProperties(label, m.Id.ToString());
        });
    }
}
