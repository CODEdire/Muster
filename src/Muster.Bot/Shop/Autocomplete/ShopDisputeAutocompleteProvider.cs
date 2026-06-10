using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Contracts;
using Muster.Persistence;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Shop.Autocomplete;

/// <summary>
/// Suggests the guild's open disputes by item name for <c>/shop orders resolve</c>, so a shop manager never types an
/// order GUID. The command's authorizer still enforces manager access.
/// </summary>
public class ShopDisputeAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(ShopDisputeAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var disputes = await db.ShopOrders.AsNoTracking()
            .Where(o => o.GuildId == guildId && o.Status == ShopOrderStatus.Disputed && o.ItemNameSnapshot.Contains(input))
            .OrderBy(o => o.StatusChangedAt)
            .Take(25)
            .Select(o => new { o.Id, o.ItemNameSnapshot })
            .ToListAsync();

        return disputes.Select(o => new ApplicationCommandOptionChoiceProperties($"{o.ItemNameSnapshot} · disputed", o.Id.ToString()));
    }
}
