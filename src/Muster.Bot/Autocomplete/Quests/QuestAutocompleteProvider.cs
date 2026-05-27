using Muster.Contracts;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
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

        var quests = await db.Quests
            .Where(m => m.GuildId == guildId
                && (m.Status == QuestStatus.Open || m.Status == QuestStatus.Scheduled || m.Status == QuestStatus.Disputed
                    || m.Status == QuestStatus.PendingApproval || m.Status == QuestStatus.PendingFinal)
                && m.Name.Contains(input))
            .OrderByDescending(m => m.CreatedAt)
            .Take(25)
            .Select(m => new { m.Id, m.Name, m.Origin })
            .ToListAsync();

        return quests.Select(q => new ApplicationCommandOptionChoiceProperties(
            $"{q.Name} [{(q.Origin == QuestOrigin.Guild ? "Guild" : "Personal")}]", q.Id.ToString()));
    }
}
