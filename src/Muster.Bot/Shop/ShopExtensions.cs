using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muster.Bot.Shop.Modules;
using Muster.Infrastructure.Messaging;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ComponentInteractions;

namespace Muster.Bot.Shop;

/// <summary>
/// Per-feature composition for the Shop slice. <see cref="AddShopFeature"/> registers background schedulers;
/// <see cref="UseShopModule"/> registers the <c>/shop</c> slash command module.
/// </summary>
public static class ShopExtensions
{
    public static IHostApplicationBuilder AddShopFeature(this IHostApplicationBuilder builder)
    {
        // Auto-settles shop orders whose buyer-confirm window has lapsed, expires offers, reveals ratings, and
        // unfeatures lapsed listings (idempotent; safe to run anywhere).
        builder.Services.AddHostedService<ShopSweepScheduler>();
        builder.Services.AddTransient<Autocomplete.ShopListingAutocompleteProvider>();
        builder.Services.AddTransient<Autocomplete.ShopCategoryAutocompleteProvider>();
        builder.Services.AddTransient<Autocomplete.ShopStoreAutocompleteProvider>();
        builder.Services.AddTransient<Autocomplete.ShopStoreTypeAutocompleteProvider>();
        builder.Services.AddTransient<Autocomplete.ShopMyOrderAutocompleteProvider>();
        builder.Services.AddTransient<Autocomplete.ShopDisputeAutocompleteProvider>();
        return builder;
    }

    public static IHost UseShopModule(this IHost host)
    {
        host.AddApplicationCommandModule<ShopModule>();

        // Browse-hub components: buy/offer/nav buttons, category/item select menus, the offer modal.
        host.AddComponentInteractionModule<ButtonInteractionContext, ShopInteractionModule>();
        host.AddComponentInteractionModule<StringMenuInteractionContext, ShopMenuInteractionModule>();
        host.AddComponentInteractionModule<ModalInteractionContext, ShopModalInteractionModule>();
        return host;
    }
}
