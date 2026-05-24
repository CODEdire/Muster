using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Autocomplete;

/// <summary>Suggests active player bounties (open or disputed) by name for bounty id parameters.</summary>
public class BountyAutocompleteProvider(IServiceScopeFactory scopeFactory)
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

        var bounties = await db.Missions
            .Where(m => m.GuildId == guildId && m.Origin == MissionOrigin.Player
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Disputed)
                && m.Name.Contains(input))
            .OrderByDescending(m => m.CreatedAt)
            .Take(25)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync();

        return bounties.Select(b => new ApplicationCommandOptionChoiceProperties(b.Name, b.Id.ToString()));
    }
}
