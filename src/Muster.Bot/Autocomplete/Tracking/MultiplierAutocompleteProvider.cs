using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Persistence;
using NetCord;
using NetCord.Services.ApplicationCommands;
using NetCord.Rest;

namespace Muster.Bot.Autocomplete;

/// <summary>Suggests the guild's reward multipliers by name (value = id), so enable/remove never need a GUID.</summary>
public class MultiplierAutocompleteProvider(IServiceScopeFactory scopeFactory)
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

        var rows = await db.RewardMultipliers
            .Where(m => m.GuildId == guildId && m.Name.Contains(input))
            .OrderBy(m => m.Name)
            .Take(25)
            .Select(m => new { m.Id, m.Name, m.Factor, m.Enabled })
            .ToListAsync();

        return rows.Select(m => new ApplicationCommandOptionChoiceProperties(
            $"{m.Name} (×{m.Factor:0.##}{(m.Enabled ? "" : ", off")})", m.Id.ToString()));
    }
}
