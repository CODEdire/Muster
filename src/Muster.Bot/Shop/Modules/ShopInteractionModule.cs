using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform.Telemetry;
using Muster.Bot.Shop.Rendering;
using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;
using Wolverine;

namespace Muster.Bot.Shop.Modules;

/// <summary>
/// Handles the ephemeral shop browse hub's components. Filtering/paging re-render the same ephemeral message in
/// place (no channel noise); Buy / Make-offer dispatch the <b>same CQRS command</b> as the web/API path with
/// <c>ActorId = the clicker</c>, so authorization + auditing are identical. custom_id carries its own state
/// (guild, category, page) — handlers are stateless. (Phase A: browse + buy/offer.)
/// </summary>
public class ShopInteractionModule(IServiceScopeFactory scopeFactory) : ComponentInteractionModule<ButtonInteractionContext>
{
    // Page nav (+ the store-mode "All shops" button): re-render the board at the requested scope/page.
    [ComponentInteraction(ShopComponentBuilder.Nav)]
    public Task Nav(ulong guildId, string scope, string sortKey, int page) => ShowBoardAsync(guildId, scope, sortKey, page);

    // Back from a detail view → page 1 of the (carried) scope + sort.
    [ComponentInteraction(ShopComponentBuilder.Back)]
    public Task Back(ulong guildId, string scope, string sortKey, int page) => ShowBoardAsync(guildId, scope, sortKey, page);

    // Hero "Browse the shop" → open this store's ephemeral browse as a fresh message (the card is a public post).
    [ComponentInteraction(ShopComponentBuilder.Home)]
    public async Task Home(ulong guildId, string storeKey)
    {
        var scopeToken = Guid.TryParseExact(storeKey, "N", out var sid)
            ? ShopComponentBuilder.ScopeOf(storeId: sid)
            : ShopComponentBuilder.ScopeAll;
        using var scope = scopeFactory.CreateScope();
        var (embed, components) = await ShopHub.BoardAsync(scope.ServiceProvider, guildId, scopeToken, ShopComponentBuilder.SortNewest, page: 1);
        await Context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties
        {
            Embeds = [embed],
            Components = components,
            Flags = MessageFlags.Ephemeral,
        }));
    }

    [ComponentInteraction(ShopComponentBuilder.MyOrders)]
    public async Task MyOrders(ulong guildId)
    {
        using var scope = scopeFactory.CreateScope();
        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        var orders = (await reads.GetPurchasesAsync(guildId, Context.User.Id))
            .Concat(await reads.GetSalesAsync(guildId, Context.User.Id))
            .OrderByDescending(o => o.CreatedAt).ToList();

        if (orders.Count == 0)
        {
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message(
                new InteractionMessageProperties { Content = "You have no orders yet.", Flags = MessageFlags.Ephemeral }));
            return;
        }

        await Context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties
        {
            Content = "Your orders — pick one to view or act on it:",
            Components = [ShopOrderComponentBuilder.MyOrders(guildId, Context.User.Id, orders)],
            Flags = MessageFlags.Ephemeral,
        }));
    }

    // ---- Order actions (My-orders receipt + DM action cards) ---------------

    [ComponentInteraction(ShopOrderComponentBuilder.Confirm)]
    public Task Confirm(ulong guildId, Guid orderId) =>
        ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory, new ConfirmReceipt(guildId, Context.User.Id, orderId), "Receipt confirmed — funds released to the seller.");

    [ComponentInteraction(ShopOrderComponentBuilder.Deliver)]
    public Task Deliver(ulong guildId, Guid orderId) =>
        ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory, new MarkDelivered(guildId, Context.User.Id, orderId), "Marked delivered.");

    [ComponentInteraction(ShopOrderComponentBuilder.Cancel)]
    public Task CancelOrder(ulong guildId, Guid orderId) =>
        ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory, new SellerCancelOrder(guildId, Context.User.Id, orderId), "Order cancelled — buyer refunded.");

    [ComponentInteraction(ShopOrderComponentBuilder.Dispute)]
    public Task Dispute(ulong guildId, Guid orderId) =>
        Context.Interaction.SendResponseAsync(InteractionCallback.Modal(ShopOrderComponentBuilder.DisputeModalFor(guildId, orderId)));

    [ComponentInteraction(ShopOrderComponentBuilder.Accept)]
    public Task Accept(ulong guildId, Guid orderId) =>
        ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory, new AcceptOffer(guildId, Context.User.Id, orderId), "Offer accepted — awaiting delivery confirmation.");

    [ComponentInteraction(ShopOrderComponentBuilder.Counter)]
    public Task Counter(ulong guildId, Guid orderId) =>
        Context.Interaction.SendResponseAsync(InteractionCallback.Modal(ShopOrderComponentBuilder.CounterModalFor(guildId, orderId)));

    [ComponentInteraction(ShopOrderComponentBuilder.Decline)]
    public Task Decline(ulong guildId, Guid orderId) =>
        ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory, new DeclineOffer(guildId, Context.User.Id, orderId), "Offer ended — buyer refunded.");

    [ComponentInteraction(ShopOrderComponentBuilder.ArbPay)]
    public Task ArbPay(ulong guildId, Guid orderId) =>
        ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory, new ArbitrateOrder(guildId, Context.User.Id, orderId, true), "Resolved — seller paid.");

    [ComponentInteraction(ShopOrderComponentBuilder.ArbRefund)]
    public Task ArbRefund(ulong guildId, Guid orderId) =>
        ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory, new ArbitrateOrder(guildId, Context.User.Id, orderId, false), "Resolved — buyer refunded.");

    [ComponentInteraction(ShopComponentBuilder.Buy)]
    public Task Buy(ulong guildId, Guid listingId) =>
        ShopInteractionDispatch.PurchaseAsync(Context.Interaction, scopeFactory,
            new PurchaseListing(guildId, Context.User.Id, listingId),
            "Purchased — your funds are held in escrow until you confirm receipt. See **🧾 My orders**.");

    [ComponentInteraction(ShopComponentBuilder.Offer)]
    public Task Offer(ulong guildId, Guid listingId) =>
        Context.Interaction.SendResponseAsync(InteractionCallback.Modal(ShopComponentBuilder.OfferModalFor(guildId, listingId)));

    // Hero featured button → open that item's detail as a fresh ephemeral (the Buy there is the purchase confirm).
    [ComponentInteraction(ShopComponentBuilder.Featured)]
    public async Task Featured(ulong guildId, string listingKey)
    {
        if (!Guid.TryParseExact(listingKey, "N", out var listingId))
        {
            return;
        }

        using var s = scopeFactory.CreateScope();
        var view = await ShopHub.ListingViewAsync(s.ServiceProvider, guildId, listingId, ShopComponentBuilder.ScopeAll, ShopComponentBuilder.SortNewest);
        if (view is not { } v)
        {
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message(
                new InteractionMessageProperties { Content = "That item is no longer available.", Flags = MessageFlags.Ephemeral }));
            return;
        }

        await Context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties
        {
            Embeds = [v.Embed],
            Components = v.Components,
            Flags = MessageFlags.Ephemeral,
        }));
    }

    /// <summary>Re-render the board ephemerally in place (same message the component sits on).</summary>
    private async Task ShowBoardAsync(ulong guildId, string scope, string sortKey, int page)
    {
        using var s = scopeFactory.CreateScope();
        var (embed, components) = await ShopHub.BoardAsync(s.ServiceProvider, guildId, scope, sortKey, page);
        await Context.Interaction.SendResponseAsync(InteractionCallback.ModifyMessage(m =>
        {
            m.Content = null;
            m.Embeds = [embed];
            m.Components = components;
        }));
    }
}

/// <summary>Select-menu handlers: pick a category (re-filter) or pick a listing (open its detail).</summary>
public class ShopMenuInteractionModule(IServiceScopeFactory scopeFactory) : ComponentInteractionModule<StringMenuInteractionContext>
{
    // Category select: the chosen value IS the scope token ("all" | "c{id}").
    [ComponentInteraction(ShopComponentBuilder.Category)]
    public Task Category(ulong guildId, string sortKey)
    {
        var scope = Context.SelectedValues.FirstOrDefault() ?? ShopComponentBuilder.ScopeAll;
        return ModifyBoardAsync(guildId, scope, sortKey);
    }

    [ComponentInteraction(ShopComponentBuilder.Sort)]
    public Task SortBy(ulong guildId, string scope)
    {
        var sortKey = Context.SelectedValues.FirstOrDefault() ?? ShopComponentBuilder.SortNewest;
        return ModifyBoardAsync(guildId, scope, sortKey);
    }

    // Store directory: the chosen value is a store id → open that store's browse (in place).
    [ComponentInteraction(ShopComponentBuilder.Directory)]
    public Task Directory(ulong guildId)
    {
        var scope = Guid.TryParseExact(Context.SelectedValues.FirstOrDefault(), "N", out var sid)
            ? ShopComponentBuilder.ScopeOf(storeId: sid)
            : ShopComponentBuilder.ScopeAll;
        return ModifyBoardAsync(guildId, scope, ShopComponentBuilder.SortNewest);
    }

    private async Task ModifyBoardAsync(ulong guildId, string scope, string sortKey)
    {
        using var s = scopeFactory.CreateScope();
        var (embed, components) = await ShopHub.BoardAsync(s.ServiceProvider, guildId, scope, sortKey, page: 1);
        await Context.Interaction.SendResponseAsync(InteractionCallback.ModifyMessage(m =>
        {
            m.Content = null;
            m.Embeds = [embed];
            m.Components = components;
        }));
    }

    // Item picker (board/search select) → open the item's detail in place.
    [ComponentInteraction(ShopComponentBuilder.Pick)]
    public async Task Pick(ulong guildId, string scope, string sortKey)
    {
        if (Context.SelectedValues.Count == 0 || !Guid.TryParse(Context.SelectedValues[0], out var listingId))
        {
            return;
        }

        using var s = scopeFactory.CreateScope();
        var view = await ShopHub.ListingViewAsync(s.ServiceProvider, guildId, listingId, scope, sortKey);
        if (view is not { } v)
        {
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message(
                new InteractionMessageProperties { Content = "That item is no longer available.", Flags = MessageFlags.Ephemeral }));
            return;
        }

        await Context.Interaction.SendResponseAsync(InteractionCallback.ModifyMessage(m =>
        {
            m.Content = null;
            m.Embeds = [v.Embed];
            m.Components = v.Components;
        }));
    }

    // Open one of my orders → an ephemeral receipt with its viewer-appropriate actions.
    [ComponentInteraction(ShopOrderComponentBuilder.OrderPick)]
    public async Task OrderPick(ulong guildId)
    {
        if (Context.SelectedValues.Count == 0 || !Guid.TryParse(Context.SelectedValues[0], out var orderId))
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var view = await ShopHub.OrderAsync(scope.ServiceProvider, guildId, orderId, Context.User.Id);
        if (view is not { } v)
        {
            await Context.Interaction.SendResponseAsync(InteractionCallback.Message(
                new InteractionMessageProperties { Content = "That order isn't available.", Flags = MessageFlags.Ephemeral }));
            return;
        }

        await Context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties
        {
            Embeds = [v.Embed],
            Components = v.Components,
            Flags = MessageFlags.Ephemeral,
        }));
    }

    // Star select on a settled order → open the (optional) comment modal carrying the chosen star count.
    [ComponentInteraction(ShopOrderComponentBuilder.RatePick)]
    public Task RatePick(ulong guildId, Guid orderId)
    {
        if (Context.SelectedValues.Count == 0 || !int.TryParse(Context.SelectedValues[0], out var stars))
        {
            return Task.CompletedTask;
        }

        return Context.Interaction.SendResponseAsync(InteractionCallback.Modal(ShopOrderComponentBuilder.RateModalFor(guildId, orderId, stars)));
    }
}

/// <summary>Modal submissions — the offer amount. Runs MakeOffer as the submitter (same funnel + ephemeral result).</summary>
public class ShopModalInteractionModule(IServiceScopeFactory scopeFactory) : ComponentInteractionModule<ModalInteractionContext>
{
    [ComponentInteraction(ShopComponentBuilder.OfferModal)]
    public Task Offer(ulong guildId, Guid listingId)
    {
        var raw = Context.Components.OfType<Label>().Select(l => l.Component)
            .OfType<TextInput>().FirstOrDefault(t => t.CustomId == ShopComponentBuilder.OfferInput)?.Value;
        if (!long.TryParse(raw, out var amount) || amount <= 0)
        {
            return Context.Interaction.SendResponseAsync(InteractionCallback.Message(
                new InteractionMessageProperties { Content = "⚠️ Enter a whole number greater than zero.", Flags = MessageFlags.Ephemeral }));
        }

        return ShopInteractionDispatch.PurchaseAsync(Context.Interaction, scopeFactory,
            new MakeOffer(guildId, Context.User.Id, listingId, amount),
            $"Offer of {amount} sent — your funds are held until the seller responds. See **🧾 My orders**.");
    }

    [ComponentInteraction(ShopOrderComponentBuilder.DisputeModal)]
    public Task Dispute(ulong guildId, Guid orderId)
    {
        var reason = Text(ShopOrderComponentBuilder.ReasonInput);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Context.Interaction.SendResponseAsync(InteractionCallback.Message(
                new InteractionMessageProperties { Content = "⚠️ A reason is required to dispute.", Flags = MessageFlags.Ephemeral }));
        }

        return ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory,
            new DisputeOrder(guildId, Context.User.Id, orderId, reason.Trim()), "Dispute raised — a shop manager will review it.");
    }

    [ComponentInteraction(ShopOrderComponentBuilder.CounterModal)]
    public Task Counter(ulong guildId, Guid orderId)
    {
        if (!long.TryParse(Text("amount"), out var amount) || amount <= 0)
        {
            return Context.Interaction.SendResponseAsync(InteractionCallback.Message(
                new InteractionMessageProperties { Content = "⚠️ Enter a whole number greater than zero.", Flags = MessageFlags.Ephemeral }));
        }

        return ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory,
            new CounterOffer(guildId, Context.User.Id, orderId, amount), $"Countered at {amount}.");
    }

    [ComponentInteraction(ShopOrderComponentBuilder.RateModal)]
    public Task Rate(ulong guildId, Guid orderId, int stars) =>
        ShopInteractionDispatch.RunAsync(Context.Interaction, scopeFactory,
            new RateOrder(guildId, Context.User.Id, orderId, stars, Text(ShopOrderComponentBuilder.CommentInput)),
            "Thanks — your rating is in (hidden until revealed).");

    private string? Text(string inputId) => Context.Components
        .OfType<Label>().Select(l => l.Component)
        .OfType<TextInput>().FirstOrDefault(t => t.CustomId == inputId)?.Value is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>Shared button/menu/modal → command dispatch for the "value" commands (buy/offer return a new id):
/// ack ephemerally, run as the clicker, report back.</summary>
internal static class ShopInteractionDispatch
{
    public static Task PurchaseAsync(Interaction interaction, IServiceScopeFactory scopeFactory, IGuildCommand command, string ok) =>
        DispatchAsync(interaction, scopeFactory, command, ok,
            (bus, cmd) => bus.InvokeAsync<Result<Guid>>(cmd).ContinueWith(t => (Result)t.Result));

    /// <summary>Dispatch a command whose handler returns a plain <see cref="Result"/> (order/offer/rating actions).</summary>
    public static Task RunAsync(Interaction interaction, IServiceScopeFactory scopeFactory, IGuildCommand command, string ok) =>
        DispatchAsync(interaction, scopeFactory, command, ok, (bus, cmd) => bus.InvokeAsync<Result>(cmd));

    private static async Task DispatchAsync(
        Interaction interaction, IServiceScopeFactory scopeFactory, IGuildCommand command, string ok,
        Func<IMessageBus, IGuildCommand, Task<Result>> invoke)
    {
        var actionKey = $"shop:{command.GetType().Name}";
        using var activity = BotTelemetry.StartInteraction(
            "interaction", actionKey, command.GuildId, interaction.User.Id, interaction.Channel?.Id);

        await interaction.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        Result result;
        try
        {
            using var scope = scopeFactory.CreateScope();
            result = await invoke(scope.ServiceProvider.GetRequiredService<IMessageBus>(), command);
            activity.SetResult(result.Ok, reason: result.Ok ? null : result.Status);
        }
        catch (Exception ex)
        {
            activity.SetException(ex);
            throw;
        }

        var content = result.Ok ? $"✅ {ok}" : $"⚠️ Couldn't do that — {result.Status}.";
        await interaction.SendFollowupMessageAsync(new InteractionMessageProperties { Content = content, Flags = MessageFlags.Ephemeral });
    }
}

/// <summary>Builds the browse-board embed + components from the read side — shared by the slash entry and the
/// re-render handlers so the hub looks identical however it's reached.</summary>
internal static class ShopHub
{
    public const int PageSize = 20;

    public static async Task<(EmbedProperties Embed, IReadOnlyList<IMessageComponentProperties> Components)> BoardAsync(
        IServiceProvider sp, ulong guildId, string scope, string sortKey, int page)
    {
        var reads = sp.GetRequiredService<IShopReadService>();
        var (categoryId, storeId) = ShopComponentBuilder.ParseScope(scope);
        var (sort, desc) = ShopComponentBuilder.ResolveSort(sortKey);
        var board = await reads.GetMarketAsync(guildId, categoryId, null, null, sort, desc, Math.Max(1, page), PageSize, storeId: storeId);

        var categories = (await reads.GetCategoriesAsync(guildId)).Select(c => new ShopCategoryOption(c.Id, c.Name)).ToList();

        // Scope label for the embed header: the store name (store mode) or category name (category filter).
        string? scopeLabel = null;
        if (storeId is { } sid)
        {
            scopeLabel = await reads.GetStoreAsync(guildId, sid) is { } st ? st.Name : "Shop";
        }
        else if (categoryId is { } cid)
        {
            scopeLabel = categories.FirstOrDefault(c => c.Id == cid)?.Name;
        }

        var embed = ShopEmbedRenderer.Board(board, scopeLabel);
        var components = ShopComponentBuilder.Board(board, categories, scope, sortKey, guildId);
        return (embed, components);
    }

    /// <summary>A listing detail view (embed + Buy/Offer/Back), or null if the listing is gone.</summary>
    public static async Task<(EmbedProperties Embed, IReadOnlyList<IMessageComponentProperties> Components)?> ListingViewAsync(
        IServiceProvider sp, ulong guildId, Guid listingId, string scope, string sortKey)
    {
        var reads = sp.GetRequiredService<IShopReadService>();
        var detail = await reads.GetListingDetailAsync(guildId, listingId);
        if (detail is null)
        {
            return null;
        }

        var baseUrl = sp.GetRequiredService<IConfiguration>()["Web:BaseUrl"];
        var embed = ShopEmbedRenderer.Listing(detail, guildId, baseUrl);
        var components = ShopComponentBuilder.Listing(detail, scope, sortKey, guildId);
        return (embed, components);
    }

    /// <summary>An order receipt view (embed + viewer-appropriate actions), or null if missing / not visible to
    /// the viewer (only buyer/seller/manager may see a receipt).</summary>
    public static Task<(EmbedProperties Embed, IReadOnlyList<IMessageComponentProperties> Components)?> OrderAsync(
        IServiceProvider sp, ulong guildId, Guid orderId, ulong viewerId)
        => OrderAsync(
            sp.GetRequiredService<IShopReadService>(),
            sp.GetRequiredService<Muster.Infrastructure.Services.Membership.GuildAuthorizationService>(),
            sp.GetRequiredService<IConfiguration>()["Web:BaseUrl"], guildId, orderId, viewerId);

    /// <summary>Explicit-dependency overload — used by the Wolverine DM handler, where injecting
    /// <see cref="IServiceProvider"/> for service location is disallowed (Wolverine codegen policy).</summary>
    public static async Task<(EmbedProperties Embed, IReadOnlyList<IMessageComponentProperties> Components)?> OrderAsync(
        IShopReadService reads, Muster.Infrastructure.Services.Membership.GuildAuthorizationService authz,
        string? baseUrl, ulong guildId, Guid orderId, ulong viewerId)
    {
        var order = await reads.GetOrderAsync(guildId, orderId);
        if (order is null)
        {
            return null;
        }

        var isManager = await authz.IsShopManagerAsync(guildId, viewerId);
        if (order.BuyerId != viewerId && order.SellerId != viewerId && !isManager)
        {
            return null;
        }

        var ratings = await reads.GetOrderRatingsAsync(guildId, orderId, viewerId);
        var embed = ShopEmbedRenderer.Order(order, guildId, baseUrl);
        var components = ShopOrderComponentBuilder.Order(guildId, order, viewerId, isManager, alreadyRated: ratings.Mine is not null, DateTimeOffset.UtcNow);
        return (embed, components);
    }
}
