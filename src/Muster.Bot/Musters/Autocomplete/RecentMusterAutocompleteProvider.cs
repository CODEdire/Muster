using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Musters.Autocomplete;

/// <summary>Suggests recent musters of <b>any</b> status by title/prompt — for read commands like
/// <c>/muster summary</c> that act on closed/expired musters too (unlike <see cref="MusterAutocompleteProvider"/>,
/// which only offers open ones for close).</summary>
public class RecentMusterAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(RecentMusterAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

        var musters = await db.ReactionMusters
            .Where(m => m.GuildId == guildId
                && ((m.Title != null && m.Title.Contains(input)) || m.Prompt.Contains(input)))
            .OrderByDescending(m => m.CreatedAt)
            .Take(25)
            .Select(m => new { m.Id, m.Title, m.Prompt, m.Status })
            .ToListAsync();

        return musters.Select(m =>
        {
            var label = string.IsNullOrWhiteSpace(m.Title) ? m.Prompt : m.Title!;
            label = $"{(label.Length <= 90 ? label : label[..89] + "…")} [{m.Status}]";
            return new ApplicationCommandOptionChoiceProperties(label, m.Id.ToString());
        });
    }
}
