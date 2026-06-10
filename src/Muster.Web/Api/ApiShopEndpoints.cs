using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using Wolverine;
using Wolverine.Http;

namespace Muster.Web.Api;

// Request bodies. None carry an actor id — every write runs as the key's bound user (ApiClient.ActsAsUserId).
public record ApiCreateStore(string Name, string? Description, string? Slug);
public record ApiEditStore(string? Name, string? Description, string? BannerImageKey, string? LogoImageKey, string? AccentColor, bool? Closed = null, Guid? StoreTypeId = null);
public record ApiCreateCategory(string Name, int Sort = 0, int? CommissionBpsOverride = null, string? Icon = null);
public record ApiEditCategory(string Name, int Sort = 0, int? CommissionBpsOverride = null, string? Icon = null);
public record ApiStoreType(string Name, int Sort = 0, string? Icon = null);
public record ApiPostListing(
    Guid StoreId, string Name, string Currency, long Price, string? Description, Guid? CategoryId,
    int Quantity = 1, string? ImageKey = null, DateTimeOffset? ExpiresAt = null, IReadOnlyList<string>? Tags = null, bool AcceptsOffers = true);
public record ApiEditListing(
    string? Name, string? Description, long? Price, Guid? CategoryId, int? Quantity, DateTimeOffset? ExpiresAt, IReadOnlyList<string>? Tags, bool? AcceptsOffers);
public record ApiBuy(int Quantity = 1);
public record ApiOffer(long Amount, int Quantity = 1);
public record ApiDispute(string? Reason, string? EvidenceKey);
public record ApiArbitrate(bool PaySeller);
public record ApiRate(int Stars, string? Comment = null);
public record ApiModerateRating(Guid RatingId, bool Hidden = true);

/// <summary>
/// Public shop API under <c>/api/v1/guilds/{guildId}/shop</c>. Auth is declarative: <see cref="RequireApiScopeAttribute"/>
/// validates the key + scope (<c>read:shop</c> / <c>write:shop</c>) and the key's bound actor
/// (<see cref="Muster.Domain.Entities.ApiClient.ActsAsUserId"/>) must hold the guild permission — the command funnel
/// authorizes that actor exactly as for the bot/web. Writes require a bound actor and invoke the same CQRS commands.
/// (Phase 1: stores, categories, listings.)
/// </summary>
public static class ApiShopEndpoints
{
    private const string Read = "read:shop";
    private const string Write = "write:shop";

    // ---- Reads -------------------------------------------------------------

    [WolverineGet("/api/v1/guilds/{guildId}/shop")]
    [RequireApiScope(Read)]
    public static async Task<IResult> Market(
        ulong guildId, IShopReadService reads, Guid? category = null, string? tag = null, string? search = null,
        string? sort = null, bool desc = true, int page = 1, int size = 24)
    {
        var board = await reads.GetMarketAsync(guildId, category, tag, search, string.IsNullOrEmpty(sort) ? "created" : sort, desc, page, size);
        return Results.Ok(new { items = board.Items, total = board.Total, page = board.Page, totalPages = board.TotalPages });
    }

    [WolverineGet("/api/v1/guilds/{guildId}/shop/stores/{slug}")]
    [RequireApiScope(Read)]
    public static async Task<IResult> Storefront(ulong guildId, string slug, IShopReadService reads)
    {
        var front = await reads.GetStorefrontAsync(guildId, slug);
        return front is null ? Results.NotFound(new { error = "store_not_found" }) : Results.Ok(front);
    }

    [WolverineGet("/api/v1/guilds/{guildId}/shop/listings/{listingId}")]
    [RequireApiScope(Read)]
    public static async Task<IResult> Listing(ulong guildId, Guid listingId, IShopReadService reads)
    {
        var listing = await reads.GetListingDetailAsync(guildId, listingId);
        return listing is null ? Results.NotFound(new { error = "listing_not_found" }) : Results.Ok(listing);
    }

    [WolverineGet("/api/v1/guilds/{guildId}/shop/categories")]
    [RequireApiScope(Read)]
    public static async Task<IResult> Categories(ulong guildId, IShopReadService reads)
    {
        var categories = await reads.GetCategoriesAsync(guildId);
        return Results.Ok(categories.Select(c => new { c.Id, c.Name, c.Sort, c.CommissionBpsOverride }));
    }

    [WolverineGet("/api/v1/guilds/{guildId}/shop/settings")]
    [RequireApiScope(Read, requireActor: true)]
    public static async Task<IResult> Settings(
        ulong guildId, HttpContext http, GuildShopSettingsService settings,
        Muster.Infrastructure.Services.Membership.GuildAuthorizationService auth)
    {
        if (!await auth.IsAdminAsync(guildId, http.ApiActor()))
        {
            return Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(await settings.GetAsync(guildId));
    }

    // ---- Writes (run as the bound actor) -----------------------------------

    [WolverinePost("/api/v1/guilds/{guildId}/shop/stores")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> CreateStore(ulong guildId, ApiCreateStore body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result<Guid>>(new CreateStore(guildId, http.ApiActor(), body.Name, body.Description, body.Slug)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/stores/{storeId}/edit")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> EditStore(ulong guildId, Guid storeId, ApiEditStore body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new EditStore(guildId, http.ApiActor(), storeId,
            Name: body.Name, Description: body.Description, BannerImageKey: body.BannerImageKey,
            LogoImageKey: body.LogoImageKey, AccentColor: body.AccentColor, Closed: body.Closed, StoreTypeId: body.StoreTypeId)));

    [WolverineDelete("/api/v1/guilds/{guildId}/shop/stores/{storeId}")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> DeleteStore(ulong guildId, Guid storeId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new DeleteStore(guildId, http.ApiActor(), storeId)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/categories")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> CreateCategory(ulong guildId, ApiCreateCategory body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result<Guid>>(new CreateCategory(guildId, http.ApiActor(), body.Name, body.Sort, body.CommissionBpsOverride, body.Icon)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/categories/{categoryId}/edit")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> EditCategory(ulong guildId, Guid categoryId, ApiEditCategory body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new EditCategory(guildId, http.ApiActor(), categoryId, body.Name, body.Sort, body.CommissionBpsOverride, body.Icon)));

    [WolverineDelete("/api/v1/guilds/{guildId}/shop/categories/{categoryId}")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> DeleteCategory(ulong guildId, Guid categoryId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new DeleteCategory(guildId, http.ApiActor(), categoryId)));

    [WolverineGet("/api/v1/guilds/{guildId}/shop/store-types")]
    [RequireApiScope(Read)]
    public static async Task<IResult> StoreTypes(ulong guildId, IShopReadService reads)
    {
        var types = await reads.GetStoreTypesAsync(guildId);
        return Results.Ok(types.Select(t => new { t.Id, t.Name, t.Sort }));
    }

    [WolverinePost("/api/v1/guilds/{guildId}/shop/store-types")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> CreateStoreType(ulong guildId, ApiStoreType body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result<Guid>>(new CreateStoreType(guildId, http.ApiActor(), body.Name, body.Sort, body.Icon)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/store-types/{storeTypeId}/edit")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> EditStoreType(ulong guildId, Guid storeTypeId, ApiStoreType body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new EditStoreType(guildId, http.ApiActor(), storeTypeId, body.Name, body.Sort, body.Icon)));

    [WolverineDelete("/api/v1/guilds/{guildId}/shop/store-types/{storeTypeId}")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> DeleteStoreType(ulong guildId, Guid storeTypeId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new DeleteStoreType(guildId, http.ApiActor(), storeTypeId)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/listings")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> PostListing(ulong guildId, ApiPostListing body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result<Guid>>(new PostListing(
            guildId, http.ApiActor(), body.StoreId, body.Name, body.Currency, body.Price, body.Description,
            body.CategoryId, body.Quantity < 1 ? 1 : body.Quantity, body.ImageKey, null, body.ExpiresAt, body.Tags, body.AcceptsOffers)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/listings/{listingId}/edit")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> EditListing(ulong guildId, Guid listingId, ApiEditListing body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new EditListing(
            guildId, http.ApiActor(), listingId, body.Name, body.Description, body.Price, body.CategoryId,
            body.Quantity, null, null, body.ExpiresAt, body.Tags, body.AcceptsOffers)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/listings/{listingId}/cancel")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> CancelListing(ulong guildId, Guid listingId, HttpContext http, IMessageBus bus, string? reason = null) =>
        ToResult(await bus.InvokeAsync<Result>(new CancelListing(guildId, http.ApiActor(), listingId, reason)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/listings/{listingId}/feature")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> FeatureListing(ulong guildId, Guid listingId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new FeatureListing(guildId, http.ApiActor(), listingId)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/listings/{listingId}/unfeature")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> UnfeatureListing(ulong guildId, Guid listingId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new UnfeatureListing(guildId, http.ApiActor(), listingId)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/resync")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> ResyncShopChannel(ulong guildId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ResyncShopChannel(guildId, http.ApiActor())));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/stores/{storeId}/resync")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> ResyncShopStore(ulong guildId, Guid storeId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ResyncShopStore(guildId, http.ApiActor(), storeId)));

    // ---- Orders (run as the bound actor) -----------------------------------

    [WolverineGet("/api/v1/guilds/{guildId}/shop/orders")]
    [RequireApiScope(Read, requireActor: true)]
    public static async Task<IResult> MyOrders(ulong guildId, HttpContext http, IShopReadService reads)
    {
        var actor = http.ApiActor();
        return Results.Ok(new
        {
            purchases = await reads.GetPurchasesAsync(guildId, actor),
            sales = await reads.GetSalesAsync(guildId, actor),
        });
    }

    [WolverineGet("/api/v1/guilds/{guildId}/shop/orders/{orderId}")]
    [RequireApiScope(Read, requireActor: true)]
    public static async Task<IResult> OrderReceipt(
        ulong guildId, Guid orderId, HttpContext http, IShopReadService reads,
        Muster.Infrastructure.Services.Membership.GuildAuthorizationService auth)
    {
        var order = await reads.GetOrderAsync(guildId, orderId);
        if (order is null)
        {
            return Results.NotFound(new { error = "order_not_found" });
        }

        var actor = http.ApiActor();
        var canView = actor == order.BuyerId || actor == order.SellerId || await auth.IsShopManagerAsync(guildId, actor);
        return canView ? Results.Ok(order) : Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/shop/listings/{listingId}/buy")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> Buy(ulong guildId, Guid listingId, ApiBuy body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result<Guid>>(new PurchaseListing(guildId, http.ApiActor(), listingId, body.Quantity < 1 ? 1 : body.Quantity)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/delivered")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> MarkDelivered(ulong guildId, Guid orderId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new MarkDelivered(guildId, http.ApiActor(), orderId)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/confirm")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> ConfirmReceipt(ulong guildId, Guid orderId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ConfirmReceipt(guildId, http.ApiActor(), orderId)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/cancel")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> CancelOrder(ulong guildId, Guid orderId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new SellerCancelOrder(guildId, http.ApiActor(), orderId)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/dispute")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> DisputeOrder(ulong guildId, Guid orderId, ApiDispute body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new DisputeOrder(guildId, http.ApiActor(), orderId, body.Reason ?? "", body.EvidenceKey)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/arbitrate")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> ArbitrateOrder(ulong guildId, Guid orderId, ApiArbitrate body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ArbitrateOrder(guildId, http.ApiActor(), orderId, body.PaySeller)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/listings/{listingId}/offer")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> MakeOffer(ulong guildId, Guid listingId, ApiOffer body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result<Guid>>(new MakeOffer(guildId, http.ApiActor(), listingId, body.Amount, body.Quantity < 1 ? 1 : body.Quantity)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/accept-offer")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> AcceptOffer(ulong guildId, Guid orderId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new AcceptOffer(guildId, http.ApiActor(), orderId)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/counter-offer")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> CounterOffer(ulong guildId, Guid orderId, ApiOffer body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new CounterOffer(guildId, http.ApiActor(), orderId, body.Amount)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/decline-offer")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> DeclineOffer(ulong guildId, Guid orderId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new DeclineOffer(guildId, http.ApiActor(), orderId)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/withdraw-offer")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> WithdrawOffer(ulong guildId, Guid orderId, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new WithdrawOffer(guildId, http.ApiActor(), orderId)));

    // ---- Ratings (run as the bound actor) ----------------------------------

    [WolverineGet("/api/v1/guilds/{guildId}/shop/orders/{orderId}/ratings")]
    [RequireApiScope(Read, requireActor: true)]
    public static async Task<IResult> OrderRatings(
        ulong guildId, Guid orderId, HttpContext http, IShopReadService reads,
        Muster.Infrastructure.Services.Membership.GuildAuthorizationService auth)
    {
        var order = await reads.GetOrderAsync(guildId, orderId);
        if (order is null)
        {
            return Results.NotFound(new { error = "order_not_found" });
        }

        var actor = http.ApiActor();
        var canView = actor == order.BuyerId || actor == order.SellerId || await auth.IsShopManagerAsync(guildId, actor);
        return canView
            ? Results.Ok(await reads.GetOrderRatingsAsync(guildId, orderId, actor))
            : Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/shop/orders/{orderId}/rate")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> RateOrder(ulong guildId, Guid orderId, ApiRate body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new RateOrder(guildId, http.ApiActor(), orderId, body.Stars, body.Comment)));

    [WolverinePost("/api/v1/guilds/{guildId}/shop/ratings/moderate")]
    [RequireApiScope(Write, requireActor: true)]
    public static async Task<IResult> ModerateRating(ulong guildId, ApiModerateRating body, HttpContext http, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<Result>(new ModerateRating(guildId, http.ApiActor(), body.RatingId, body.Hidden)));

    [WolverineGet("/api/v1/guilds/{guildId}/shop/disputes")]
    [RequireApiScope(Read, requireActor: true)]
    public static async Task<IResult> Disputes(
        ulong guildId, HttpContext http, IShopReadService reads,
        Muster.Infrastructure.Services.Membership.GuildAuthorizationService auth)
    {
        if (!await auth.IsShopManagerAsync(guildId, http.ApiActor()))
        {
            return Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(await reads.GetDisputesAsync(guildId));
    }

    // ---- Helpers -----------------------------------------------------------

    private static IResult ToResult(Result result) =>
        result.Ok
            ? Results.Ok(new { ok = true })
            : Results.Json(new { error = "shop_action_failed", reason = result.Status }, statusCode: StatusCodes.Status409Conflict);

    private static IResult ToResult(Result<Guid> result) =>
        result.Ok
            ? Results.Ok(new { ok = true, id = result.Value })
            : Results.Json(new { error = "shop_action_failed", reason = result.Status }, statusCode: StatusCodes.Status409Conflict);
}
