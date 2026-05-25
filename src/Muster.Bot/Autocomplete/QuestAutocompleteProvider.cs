using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Autocomplete;

/// <summary>Suggests active quests (any origin) by name for quest id parameters, so users never type a GUID.</summary>
public class QuestAutocompleteProvider(IServiceScopeFactory scopeFactory)
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
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

        var quests = await db.Missions
            .Where(m => m.GuildId == guildId && m.Type == MissionType.Quest
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Scheduled || m.Status == MissionStatus.Disputed
                    || m.Status == MissionStatus.PendingApproval || m.Status == MissionStatus.PendingFinal)
                && m.Name.Contains(input))
            .OrderByDescending(m => m.CreatedAt)
            .Take(25)
            .Select(m => new { m.Id, m.Name, m.Origin })
            .ToListAsync();

        return quests.Select(q => new ApplicationCommandOptionChoiceProperties(
            $"{q.Name} [{(q.Origin == MissionOrigin.Guild ? "Guild" : "Personal")}]", q.Id.ToString()));
    }
}
