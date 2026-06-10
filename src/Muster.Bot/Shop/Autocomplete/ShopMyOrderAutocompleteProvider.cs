using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Shop.Autocomplete;

/// <summary>
/// Suggests the caller's own orders (as buyer or seller) by item name for the <c>/shop orders …</c> commands, so a
/// member never types an order GUID. Scoped to the actor — the command's server-side authorizer still enforces who
/// may act on each order.
/// </summary>
public class ShopMyOrderAutocompleteProvider(IServiceScopeFactory scopeFactory)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(ShopMyOrderAutocompleteProvider));

        if (context.Interaction.GuildId is not { } guildId)
        {
            return [];
        }

        var input = option.Value ?? string.Empty;
        var userId = context.Interaction.User.Id;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var orders = await db.ShopOrders.AsNoTracking()
            .Where(o => o.GuildId == guildId && (o.BuyerId == userId || o.SellerId == userId)
                && o.ItemNameSnapshot.Contains(input))
            .OrderByDescending(o => o.CreatedAt)
            .Take(25)
            .Select(o => new { o.Id, o.ItemNameSnapshot, o.Status })
            .ToListAsync();

        return orders.Select(o => new ApplicationCommandOptionChoiceProperties($"{o.ItemNameSnapshot} · {o.Status}", o.Id.ToString()));
    }
}
