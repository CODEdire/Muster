using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Shops;
using Muster.Infrastructure.Services.Shops;
using Muster.Infrastructure.Services.Membership;
using Muster.Bot.Platform;
using Muster.Bot.Shop.Autocomplete;
using Muster.Bot.Shop.Rendering;
using Muster.Infrastructure.Services.Platform;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using Wolverine;

namespace Muster.Bot.Shop.Modules;

/// <summary>
/// Discord adapter for the player marketplace, under the <c>/shop</c> root. Discovery subcommands (browse, stores,
/// search) sit directly under <c>/shop</c>; management is grouped: <c>/shop store …</c>, <c>/shop listing …</c>,
/// <c>/shop orders …</c>. Browsing is open to any member; selling needs <c>ShopCreator</c>; arbitration needs
/// <c>ShopManager</c>. Category/store-type vocab + settings + images live on the web admin only. Each write is
/// dispatched as a CQRS command via the bus — the same funnel the web and API use. Every command is feature-gated:
/// management/discovery require the shop to be Enabled; order wind-down only requires it not be platform/plan-blocked.
/// </summary>
[SlashCommand("shop", "Player marketplace: browse, run stores, and trade.")]
public class ShopModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    // ---- Discovery (direct subcommands) ------------------------------------

    [SubSlashCommand("browse", "Browse the shop — a private, filterable catalogue you can buy from.")]
    public async Task BrowseAsync(
        [SlashCommandParameter(Name = "store", Description = "Limit to one shop (optional)", AutocompleteProviderType = typeof(ShopStoreAutocompleteProvider))] string store = "")
    {
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        if (Context.Guild is not { } guild)
        {
            await Context.Interaction.ModifyResponseAsync(m => m.Content = "Use this in a server.");
            return;
        }

        var scopeToken = Guid.TryParse(store, out var sid) ? ShopComponentBuilder.ScopeOf(storeId: sid) : ShopComponentBuilder.ScopeAll;
        using var s = scopeFactory.CreateScope();
        if (!await FeatureEnabledAsync(s.ServiceProvider, guild.Id, PlatformFeature.Shop))
        {
            await Context.Interaction.ModifyResponseAsync(m => m.Content = FeatureOffMessage);
            return;
        }

        var (embed, components) = await ShopHub.BoardAsync(s.ServiceProvider, guild.Id, scopeToken, ShopComponentBuilder.SortNewest, page: 1);
        await Context.Interaction.ModifyResponseAsync(m => { m.Embeds = [embed]; m.Components = components; });
    }

    [SubSlashCommand("stores", "Find a shop to browse — or list your own with mine:true.")]
    public async Task StoresAsync(
        [SlashCommandParameter(Name = "mine", Description = "Show only your shops")] bool mine = false)
    {
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        if (Context.Guild is not { } guild)
        {
            await Context.Interaction.ModifyResponseAsync(m => m.Content = "Use this in a server.");
            return;
        }

        using var s = scopeFactory.CreateScope();
        if (!await FeatureEnabledAsync(s.ServiceProvider, guild.Id, PlatformFeature.Shop))
        {
            await Context.Interaction.ModifyResponseAsync(m => m.Content = FeatureOffMessage);
            return;
        }

        var reads = s.ServiceProvider.GetRequiredService<IShopReadService>();
        var page = await reads.GetStoresAsync(guild.Id, mine ? Context.User.Id : null, includeClosed: false, null, "name", desc: false, 1, 25);
        if (page.Items.Count == 0)
        {
            await Context.Interaction.ModifyResponseAsync(m => m.Content = mine
                ? "You have no shops. Open one with `/shop store create`."
                : "No shops yet.");
            return;
        }

        var options = page.Items.Select(r => new ShopStoreOption(r.Id, r.Name, r.ListingCount)).ToList();
        await Context.Interaction.ModifyResponseAsync(m =>
        {
            m.Content = mine ? "Your shops — pick one to browse:" : "Pick a shop to browse:";
            m.Components = [ShopComponentBuilder.StoreDirectory(guild.Id, options)];
        });
    }

    [SubSlashCommand("search", "Search items for sale by name.")]
    public async Task SearchAsync(
        [SlashCommandParameter(Name = "term", Description = "What to search for")] string term)
    {
        await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        if (Context.Guild is not { } guild)
        {
            await Context.Interaction.ModifyResponseAsync(m => m.Content = "Use this in a server.");
            return;
        }

        using var s = scopeFactory.CreateScope();
        if (!await FeatureEnabledAsync(s.ServiceProvider, guild.Id, PlatformFeature.Shop))
        {
            await Context.Interaction.ModifyResponseAsync(m => m.Content = FeatureOffMessage);
            return;
        }

        var board = await s.ServiceProvider.GetRequiredService<IShopReadService>()
            .GetMarketAsync(guild.Id, null, null, NullIfBlank(term), "created", desc: true, 1, 25);
        if (board.Items.Count == 0)
        {
            await Context.Interaction.ModifyResponseAsync(m => m.Content = $"No items match “{term}”.");
            return;
        }

        var pick = $"{ShopComponentBuilder.Pick}:{guild.Id}:{ShopComponentBuilder.ScopeAll}:{ShopComponentBuilder.SortNewest}";
        await Context.Interaction.ModifyResponseAsync(m =>
        {
            m.Content = $"{board.Total} result(s) for “{term}” — pick one to view:";
            m.Components = [ShopComponentBuilder.ItemSelect(board.Items, pick)];
        });
    }

    [SubSlashCommand("resync", "Re-post all featured listing cards to the shop channel.")]
    public Task ResyncAsync()
        => RunAsync(async (sp, guildId) =>
        {
            var bus = sp.GetRequiredService<IMessageBus>();
            var result = await bus.InvokeAsync<Result>(new ResyncShopChannel(guildId, Context.User.Id));
            return result.ToCommandResult("Shop cards re-synced.");
        }, RequiredRole.ShopCreator, auditAction: "shop.resync", feature: PlatformFeature.Shop);

    // ---- Shared helpers (accessible to the nested group modules) -----------

    internal static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static IReadOnlyList<string>? ParseTags(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }

        var list = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length == 0 ? null : list;
    }

    // ---- /shop store … -----------------------------------------------------

    [SubSlashCommand("store", "Open and manage your storefronts.")]
    public class StoreModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
    {
        [SubSlashCommand("create", "Open a new storefront.")]
        public Task CreateAsync(
            [SlashCommandParameter(Name = "name", Description = "Store name")] string name)
            => RunAsync(async (sp, guildId) =>
            {
                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result<Guid>>(new CreateStore(guildId, Context.User.Id, name));
                return ((Result)result).ToCommandResult($"Store **{name}** created.");
            }, RequiredRole.ShopCreator, auditAction: "shop.storeCreate", feature: PlatformFeature.Shop);

        [SubSlashCommand("edit", "Rename / re-describe / re-type one of your stores.")]
        public Task EditAsync(
            [SlashCommandParameter(Name = "store", Description = "Your store's handle (slug)")] string storeSlug,
            [SlashCommandParameter(Name = "name", Description = "New name")] string name = "",
            [SlashCommandParameter(Name = "description", Description = "New description")] string description = "",
            [SlashCommandParameter(Name = "type", Description = "Shop type", AutocompleteProviderType = typeof(ShopStoreTypeAutocompleteProvider))] string type = "")
            => RunAsync(async (sp, guildId) =>
            {
                var store = await sp.GetRequiredService<MusterDbContext>().FindStoreBySlugAsync(guildId, storeSlug);
                if (store is null)
                {
                    return CommandResult.Error($"No store with handle `{storeSlug.Trim().ToLowerInvariant()}`. See `/shop stores`.");
                }

                Guid? storeTypeId = Guid.TryParse(type, out var tid) ? tid : null;
                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new EditStore(
                    guildId, Context.User.Id, store.Id, Name: NullIfBlank(name), Description: NullIfBlank(description), StoreTypeId: storeTypeId));
                return result.ToCommandResult("Store updated.");
            }, RequiredRole.ShopCreator, auditAction: "shop.storeEdit", feature: PlatformFeature.Shop);

        [SubSlashCommand("open", "Reopen a closed store (resume new listings / sales).")]
        public Task OpenAsync(
            [SlashCommandParameter(Name = "store", Description = "Your store's handle (slug)")] string storeSlug)
            => SetClosedAsync(storeSlug, closed: false, "Store reopened.");

        [SubSlashCommand("close", "Close a store (keeps history; stops new listings, purchases, and offers).")]
        public Task CloseAsync(
            [SlashCommandParameter(Name = "store", Description = "Your store's handle (slug)")] string storeSlug)
            => SetClosedAsync(storeSlug, closed: true, "Store closed.");

        private Task SetClosedAsync(string storeSlug, bool closed, string ok)
            => RunAsync(async (sp, guildId) =>
            {
                var store = await sp.GetRequiredService<MusterDbContext>().FindStoreBySlugAsync(guildId, storeSlug);
                if (store is null)
                {
                    return CommandResult.Error($"No store with handle `{storeSlug.Trim().ToLowerInvariant()}`. See `/shop stores`.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new EditStore(guildId, Context.User.Id, store.Id, Closed: closed));
                return result.ToCommandResult(ok);
            }, RequiredRole.ShopCreator, auditAction: "shop.storeClose", feature: PlatformFeature.Shop);

        [SubSlashCommand("delete", "Delete a store and all its listings (can't be undone; refused with live orders).")]
        public Task DeleteAsync(
            [SlashCommandParameter(Name = "store", Description = "Your store's handle (slug)")] string storeSlug)
            => RunAsync(async (sp, guildId) =>
            {
                var store = await sp.GetRequiredService<MusterDbContext>().FindStoreBySlugAsync(guildId, storeSlug);
                if (store is null)
                {
                    return CommandResult.Error($"No store with handle `{storeSlug.Trim().ToLowerInvariant()}`. See `/shop stores`.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new DeleteStore(guildId, Context.User.Id, store.Id));
                return result.ToCommandResult($"Store **{store.Name}** deleted.");
            }, RequiredRole.ShopCreator, auditAction: "shop.storeDelete", feature: PlatformFeature.Shop);

        [SubSlashCommand("resync", "Re-post this store's featured cards to the shop channel.")]
        public Task ResyncAsync(
            [SlashCommandParameter(Name = "store", Description = "Your store's handle (slug)")] string storeSlug)
            => RunAsync(async (sp, guildId) =>
            {
                var store = await sp.GetRequiredService<MusterDbContext>().FindStoreBySlugAsync(guildId, storeSlug);
                if (store is null)
                {
                    return CommandResult.Error($"No store with handle `{storeSlug.Trim().ToLowerInvariant()}`. See `/shop stores`.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new ResyncShopStore(guildId, Context.User.Id, store.Id));
                return result.ToCommandResult("Shop cards re-synced for this store.");
            }, RequiredRole.ShopCreator, auditAction: "shop.storeResync", feature: PlatformFeature.Shop);
    }

    // ---- /shop listing … ---------------------------------------------------

    [SubSlashCommand("listing", "List and manage items for sale.")]
    public class ListingModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
    {
        [SubSlashCommand("add", "List an item for sale in one of your stores.")]
        public Task AddAsync(
            [SlashCommandParameter(Name = "store", Description = "Your store's handle (slug)")] string storeSlug,
            [SlashCommandParameter(Name = "name", Description = "Item name")] string name,
            [SlashCommandParameter(Name = "currency", Description = "Currency code (e.g. COIN)")] string currency,
            [SlashCommandParameter(Name = "price", Description = "Ask price")] long price,
            [SlashCommandParameter(Name = "description", Description = "Item description")] string description = "",
            [SlashCommandParameter(Name = "category", Description = "Category", AutocompleteProviderType = typeof(ShopCategoryAutocompleteProvider))] string category = "",
            [SlashCommandParameter(Name = "quantity", Description = "Units available (default 1)")] long quantity = 1,
            [SlashCommandParameter(Name = "tags", Description = "Comma-separated tags")] string tags = "",
            [SlashCommandParameter(Name = "accepts_offers", Description = "Allow price offers (default true)")] bool acceptsOffers = true,
            [SlashCommandParameter(Name = "expires", Description = "When it delists — your local time (set /timezone), or `in 7 days` (optional)")] string expires = "")
            => RunAsync(async (sp, guildId) =>
            {
                var db = sp.GetRequiredService<MusterDbContext>();
                var store = await db.FindStoreBySlugAsync(guildId, storeSlug);
                if (store is null)
                {
                    return CommandResult.Error($"No store with handle `{storeSlug.Trim().ToLowerInvariant()}`. See `/shop stores`.");
                }

                Guid? categoryId = Guid.TryParse(category, out var cid) ? cid : null;
                var (okExpires, expiresAt, expiresErr) = await sp.GetRequiredService<TimeZoneService>()
                    .ParseLocalAsync(guildId, Context.User.Id, NullIfBlank(expires));
                if (!okExpires)
                {
                    return CommandResult.Error(expiresErr!);
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result<Guid>>(new PostListing(
                    guildId, Context.User.Id, store.Id, name, currency, price, NullIfBlank(description),
                    CategoryId: categoryId, Quantity: (int)Math.Max(1, quantity), AcceptsOffers: acceptsOffers, ExpiresAt: expiresAt, Tags: ParseTags(tags)));
                return ((Result)result).ToCommandResult($"Listed **{name}** for {price} {currency.ToUpperInvariant()}.");
            }, RequiredRole.ShopCreator, auditAction: "shop.listItem", feature: PlatformFeature.Shop);

        [SubSlashCommand("edit", "Edit one of your listings (before it has orders).")]
        public Task EditAsync(
            [SlashCommandParameter(Name = "listing", Description = "Your listing", AutocompleteProviderType = typeof(ShopListingAutocompleteProvider))] string listing,
            [SlashCommandParameter(Name = "name", Description = "New name")] string name = "",
            [SlashCommandParameter(Name = "description", Description = "New description")] string description = "",
            [SlashCommandParameter(Name = "price", Description = "New price")] long price = 0,
            [SlashCommandParameter(Name = "quantity", Description = "New quantity")] long quantity = 0,
            [SlashCommandParameter(Name = "accepts_offers", Description = "Allow price offers")] bool? acceptsOffers = null)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(listing, out var listingId))
                {
                    return CommandResult.Error("That doesn't look like a valid listing.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new EditListing(
                    guildId, Context.User.Id, listingId, NullIfBlank(name), NullIfBlank(description),
                    price > 0 ? price : null, null, quantity > 0 ? (int)quantity : null, null, null, null, null, acceptsOffers));
                return result.ToCommandResult("Listing updated.");
            }, RequiredRole.ShopCreator, auditAction: "shop.listingEdit", feature: PlatformFeature.Shop);

        [SubSlashCommand("cancel", "Withdraw one of your listings.")]
        public Task CancelAsync(
            [SlashCommandParameter(Name = "listing", Description = "Your listing", AutocompleteProviderType = typeof(ShopListingAutocompleteProvider))] string listing)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(listing, out var listingId))
                {
                    return CommandResult.Error("That doesn't look like a valid listing.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new CancelListing(guildId, Context.User.Id, listingId));
                return result.ToCommandResult("Listing withdrawn.");
            }, RequiredRole.ShopCreator, auditAction: "shop.listingCancel", feature: PlatformFeature.Shop);

        [SubSlashCommand("feature", "Promote one of your listings to the shop channel (burns the guild's featured fee).")]
        public Task FeatureAsync(
            [SlashCommandParameter(Name = "listing", Description = "Your listing to feature", AutocompleteProviderType = typeof(ShopListingAutocompleteProvider))] string listing)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(listing, out var listingId))
                {
                    return CommandResult.Error("That doesn't look like a valid listing.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new FeatureListing(guildId, Context.User.Id, listingId));
                return result.ToCommandResult("Listing featured — it's now promoted in the shop channel.");
            }, RequiredRole.ShopCreator, auditAction: "shop.feature", feature: PlatformFeature.Shop);

        [SubSlashCommand("unfeature", "Stop featuring one of your listings (the fee is not refunded).")]
        public Task UnfeatureAsync(
            [SlashCommandParameter(Name = "listing", Description = "Your featured listing", AutocompleteProviderType = typeof(ShopListingAutocompleteProvider))] string listing)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(listing, out var listingId))
                {
                    return CommandResult.Error("That doesn't look like a valid listing.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new UnfeatureListing(guildId, Context.User.Id, listingId));
                return result.ToCommandResult("Listing un-featured.");
            }, RequiredRole.ShopCreator, auditAction: "shop.unfeature", feature: PlatformFeature.Shop);

        [SubSlashCommand("add-stock", "Add more units to one of your active listings (works even with live orders).")]
        public Task AddStockAsync(
            [SlashCommandParameter(Name = "listing", Description = "Your active listing", AutocompleteProviderType = typeof(ShopListingAutocompleteProvider))] string listing,
            [SlashCommandParameter(Name = "units", Description = "How many units to add")] long units)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(listing, out var listingId))
                {
                    return CommandResult.Error("That doesn't look like a valid listing.");
                }

                if (units < 1)
                {
                    return CommandResult.Error("Enter a whole number of units (1 or more).");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new AddListingStock(guildId, Context.User.Id, listingId, (int)units));
                return result.ToCommandResult($"Added {units} to stock.");
            }, RequiredRole.ShopCreator, auditAction: "shop.listingAddStock", feature: PlatformFeature.Shop);

        [SubSlashCommand("relist", "Relist a sold-out or expired listing as a fresh copy with new stock.")]
        public Task RelistAsync(
            [SlashCommandParameter(Name = "listing", Description = "Your sold-out / expired listing", AutocompleteProviderType = typeof(ShopRelistableListingAutocompleteProvider))] string listing,
            [SlashCommandParameter(Name = "units", Description = "Units to stock the new copy with")] long units,
            [SlashCommandParameter(Name = "price", Description = "New price (leave blank to keep the old one)")] long price = 0)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(listing, out var listingId))
                {
                    return CommandResult.Error("That doesn't look like a valid listing.");
                }

                if (units < 1)
                {
                    return CommandResult.Error("Enter a whole number of units (1 or more).");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result<Guid>>(new RelistListing(guildId, Context.User.Id, listingId, (int)units, price > 0 ? price : null));
                return ((Result)result).ToCommandResult("Relisted — a fresh copy is now live.");
            }, RequiredRole.ShopCreator, auditAction: "shop.listingRelist", feature: PlatformFeature.Shop);
    }

    // ---- /shop orders … ----------------------------------------------------

    [SubSlashCommand("orders", "View your orders, act on them, and (managers) resolve disputes.")]
    public class OrdersModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
    {
        public enum ResolveDecision { Pay, Refund }

        [SubSlashCommand("list", "Your orders — pick one to act on (confirm, dispute, rate, respond to offers).")]
        public async Task ListAsync()
        {
            await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
            if (Context.Guild is not { } guild)
            {
                await Context.Interaction.ModifyResponseAsync(m => m.Content = "Use this in a server.");
                return;
            }

            using var s = scopeFactory.CreateScope();
            if (!await FeatureEnabledAsync(s.ServiceProvider, guild.Id, PlatformFeature.Shop, windDown: true))
            {
                await Context.Interaction.ModifyResponseAsync(m => m.Content = FeatureOffMessage);
                return;
            }

            var reads = s.ServiceProvider.GetRequiredService<IShopReadService>();
            var orders = (await reads.GetPurchasesAsync(guild.Id, Context.User.Id))
                .Concat(await reads.GetSalesAsync(guild.Id, Context.User.Id))
                .OrderByDescending(o => o.CreatedAt).ToList();
            if (orders.Count == 0)
            {
                await Context.Interaction.ModifyResponseAsync(m => m.Content = "You have no orders yet.");
                return;
            }

            await Context.Interaction.ModifyResponseAsync(m =>
            {
                m.Content = "Your orders — pick one to view or act on it:";
                m.Components = [ShopOrderComponentBuilder.MyOrders(guild.Id, Context.User.Id, orders)];
            });
        }

        [SubSlashCommand("disputes", "Open disputes awaiting arbitration — pick one to resolve (shop managers).")]
        public async Task DisputesAsync()
        {
            await Context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
            if (Context.Guild is not { } guild)
            {
                await Context.Interaction.ModifyResponseAsync(m => m.Content = "Use this in a server.");
                return;
            }

            using var s = scopeFactory.CreateScope();
            if (!await FeatureEnabledAsync(s.ServiceProvider, guild.Id, PlatformFeature.Shop, windDown: true))
            {
                await Context.Interaction.ModifyResponseAsync(m => m.Content = FeatureOffMessage);
                return;
            }

            if (!await s.ServiceProvider.GetRequiredService<GuildAuthorizationService>().IsShopManagerAsync(guild.Id, Context.User.Id))
            {
                await Context.Interaction.ModifyResponseAsync(m => m.Content = "You need shop-manager access to arbitrate disputes.");
                return;
            }

            var disputes = await s.ServiceProvider.GetRequiredService<IShopReadService>().GetDisputesAsync(guild.Id);
            if (disputes.Count == 0)
            {
                await Context.Interaction.ModifyResponseAsync(m => m.Content = "No open disputes. 🎉");
                return;
            }

            await Context.Interaction.ModifyResponseAsync(m =>
            {
                m.Content = "Open disputes — pick one to review and resolve:";
                m.Components = [ShopOrderComponentBuilder.MyOrders(guild.Id, Context.User.Id, disputes)];
            });
        }

        // Direct (scriptable) order actions — the UI-based flow above remains for point-and-click.

        [SubSlashCommand("confirm", "Confirm you received an order — releases escrow to the seller.")]
        public Task ConfirmAsync(
            [SlashCommandParameter(Name = "order", Description = "Your order", AutocompleteProviderType = typeof(ShopMyOrderAutocompleteProvider))] string order)
            => OrderActionAsync(order, (g, u, o) => new ConfirmReceipt(g, u, o), "Receipt confirmed — funds released to the seller.", "shop.confirm");

        [SubSlashCommand("deliver", "Mark one of your sales delivered (two-step delivery).")]
        public Task DeliverAsync(
            [SlashCommandParameter(Name = "order", Description = "An order you're selling", AutocompleteProviderType = typeof(ShopMyOrderAutocompleteProvider))] string order)
            => OrderActionAsync(order, (g, u, o) => new MarkDelivered(g, u, o), "Marked delivered.", "shop.deliver");

        [SubSlashCommand("cancel", "Cancel a pending order you're selling and refund the buyer.")]
        public Task CancelAsync(
            [SlashCommandParameter(Name = "order", Description = "An order you're selling", AutocompleteProviderType = typeof(ShopMyOrderAutocompleteProvider))] string order)
            => OrderActionAsync(order, (g, u, o) => new SellerCancelOrder(g, u, o), "Order cancelled — buyer refunded.", "shop.orderCancel");

        [SubSlashCommand("dispute", "Open a dispute on one of your orders (a shop manager will resolve it).")]
        public Task DisputeAsync(
            [SlashCommandParameter(Name = "order", Description = "Your order", AutocompleteProviderType = typeof(ShopMyOrderAutocompleteProvider))] string order,
            [SlashCommandParameter(Name = "reason", Description = "What's wrong?")] string reason)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(order, out var orderId))
                {
                    return CommandResult.Error("That doesn't look like a valid order.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new DisputeOrder(guildId, Context.User.Id, orderId, reason, null));
                return result.ToCommandResult("Dispute raised — a shop manager will review it.");
            }, auditAction: "shop.dispute", feature: PlatformFeature.Shop, featureWindDown: true);

        [SubSlashCommand("resolve", "Resolve an open dispute — pay the seller or refund the buyer (shop managers).")]
        public Task ResolveAsync(
            [SlashCommandParameter(Name = "order", Description = "A disputed order", AutocompleteProviderType = typeof(ShopDisputeAutocompleteProvider))] string order,
            [SlashCommandParameter(Name = "decision", Description = "Pay the seller or refund the buyer")] ResolveDecision decision)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(order, out var orderId))
                {
                    return CommandResult.Error("That doesn't look like a valid order.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(new ArbitrateOrder(guildId, Context.User.Id, orderId, decision == ResolveDecision.Pay));
                return result.ToCommandResult(decision == ResolveDecision.Pay ? "Resolved — seller paid." : "Resolved — buyer refunded.");
            }, RequiredRole.ShopManager, auditAction: "shop.arbitrate", feature: PlatformFeature.Shop, featureWindDown: true);

        private Task OrderActionAsync(string order, Func<ulong, ulong, Guid, IGuildCommand> make, string ok, string auditAction)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(order, out var orderId))
                {
                    return CommandResult.Error("That doesn't look like a valid order.");
                }

                var bus = sp.GetRequiredService<IMessageBus>();
                var result = await bus.InvokeAsync<Result>(make(guildId, Context.User.Id, orderId));
                return result.ToCommandResult(ok);
            }, auditAction: auditAction, feature: PlatformFeature.Shop, featureWindDown: true);
    }
}
