using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// CQRS command handlers for the shop / player marketplace. Every surface (bot/web/api) invokes these through
/// the Wolverine bus, so they run inside the transactional/outbox pipeline and share one enforcement point:
/// the handler loads the resource, authorizes via <see cref="IShopAuthorizer"/>, then delegates to
/// <see cref="IShopService"/>. They return a <see cref="Result"/> (reason = the domain <see cref="ShopResult"/>
/// name) which adapters map to user-facing text. Unit-testable as plain methods. (Phase 1: stores/categories/listings.)
/// </summary>
public static class CreateStoreHandler
{
    public static async Task<Result<Guid>> Handle(
        CreateStore command, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var actor = new GuildActor(command.GuildId, command.ActorId);
        if (!await authorizer.AuthorizeAsync(actor, command.GuildId, ShopPermission.ManageStore, ct))
        {
            return Result<Guid>.Fail(nameof(ShopResult.Forbidden));
        }

        var (result, storeId) = await shop.CreateStoreAsync(
            command.GuildId, command.ActorId, command.Name, command.Description, command.Slug, command.StoreTypeId, ct);
        return result == ShopResult.Ok ? Result<Guid>.Success(storeId!.Value) : Result<Guid>.Fail(result.ToString());
    }
}

public static class EditStoreHandler
{
    public static async Task<Result> Handle(
        EditStore command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var store = await db.FindStoreInGuildAsync(command.GuildId, command.StoreId, ct);
        if (store is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), store, ShopPermission.ManageStore, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.EditStoreAsync(
            store, command.Name, command.Description, command.BannerImageKey, command.LogoImageKey, command.AccentColor, command.Closed, command.StoreTypeId, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class DeleteStoreHandler
{
    public static async Task<Result> Handle(
        DeleteStore command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var store = await db.FindStoreInGuildAsync(command.GuildId, command.StoreId, ct);
        if (store is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), store, ShopPermission.ManageStore, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.DeleteStoreAsync(store, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class CreateCategoryHandler
{
    public static async Task<Result<Guid>> Handle(
        CreateCategory command, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var actor = new GuildActor(command.GuildId, command.ActorId);
        if (!await authorizer.AuthorizeAsync(actor, command.GuildId, ShopPermission.ManageCategories, ct))
        {
            return Result<Guid>.Fail(nameof(ShopResult.Forbidden));
        }

        var (result, categoryId) = await shop.CreateCategoryAsync(
            command.GuildId, command.Name, command.Sort, command.CommissionBpsOverride, command.Icon, ct);
        return result == ShopResult.Ok ? Result<Guid>.Success(categoryId!.Value) : Result<Guid>.Fail(result.ToString());
    }
}

public static class EditCategoryHandler
{
    public static async Task<Result> Handle(
        EditCategory command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var actor = new GuildActor(command.GuildId, command.ActorId);
        if (!await authorizer.AuthorizeAsync(actor, command.GuildId, ShopPermission.ManageCategories, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var category = await db.FindCategoryInGuildAsync(command.GuildId, command.CategoryId, ct);
        if (category is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        var result = await shop.EditCategoryAsync(category, command.Name, command.Sort, command.CommissionBpsOverride, command.Icon, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class DeleteCategoryHandler
{
    public static async Task<Result> Handle(
        DeleteCategory command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var actor = new GuildActor(command.GuildId, command.ActorId);
        if (!await authorizer.AuthorizeAsync(actor, command.GuildId, ShopPermission.ManageCategories, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var category = await db.FindCategoryInGuildAsync(command.GuildId, command.CategoryId, ct);
        if (category is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        var result = await shop.DeleteCategoryAsync(category, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class SeedGuildDefaultsHandler
{
    public static async Task<Result> Handle(
        SeedGuildDefaults command, IShopAuthorizer authorizer, GuildSeedService seed, CancellationToken ct)
    {
        // Seeding the shared taxonomy is admin-gated (same gate as managing categories/types).
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), command.GuildId, ShopPermission.ManageCategories, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        await seed.ApplyAsync(command.GuildId, command.Restore, ct);
        return Result.Success();
    }
}

public static class CreateStoreTypeHandler
{
    public static async Task<Result<Guid>> Handle(
        CreateStoreType command, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), command.GuildId, ShopPermission.ManageCategories, ct))
        {
            return Result<Guid>.Fail(nameof(ShopResult.Forbidden));
        }

        var (result, id) = await shop.CreateStoreTypeAsync(command.GuildId, command.Name, command.Sort, command.Icon, ct);
        return result == ShopResult.Ok ? Result<Guid>.Success(id!.Value) : Result<Guid>.Fail(result.ToString());
    }
}

public static class EditStoreTypeHandler
{
    public static async Task<Result> Handle(
        EditStoreType command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), command.GuildId, ShopPermission.ManageCategories, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var type = await db.FindStoreTypeInGuildAsync(command.GuildId, command.StoreTypeId, ct);
        if (type is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        var result = await shop.EditStoreTypeAsync(type, command.Name, command.Sort, command.Icon, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class DeleteStoreTypeHandler
{
    public static async Task<Result> Handle(
        DeleteStoreType command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), command.GuildId, ShopPermission.ManageCategories, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var type = await db.FindStoreTypeInGuildAsync(command.GuildId, command.StoreTypeId, ct);
        if (type is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        var result = await shop.DeleteStoreTypeAsync(type, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class PostListingHandler
{
    public static async Task<Result<Guid>> Handle(
        PostListing command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var store = await db.FindStoreInGuildAsync(command.GuildId, command.StoreId, ct);
        if (store is null)
        {
            return Result<Guid>.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), store, ShopPermission.CreateListing, ct))
        {
            return Result<Guid>.Fail(nameof(ShopResult.Forbidden));
        }

        var (result, listingId) = await shop.PostListingAsync(
            store, command.ActorId, command.Name, command.Currency, command.Price, command.Description,
            command.CategoryId, command.Quantity, command.AcceptsOffers, command.ImageKey, command.ThumbKey, command.ExpiresAt, command.Tags, ct);
        return result == ShopResult.Ok ? Result<Guid>.Success(listingId!.Value) : Result<Guid>.Fail(result.ToString());
    }
}

public static class EditListingHandler
{
    public static async Task<Result> Handle(
        EditListing command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var listing = await db.FindListingInGuildAsync(command.GuildId, command.ListingId, ct);
        if (listing is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), listing, ShopPermission.EditListing, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.EditListingAsync(
            listing, command.Name, command.Description, command.Price, command.CategoryId, command.Quantity,
            command.AcceptsOffers, command.ImageKey, command.ThumbKey, command.ExpiresAt, command.Tags, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class CancelListingHandler
{
    public static async Task<Result> Handle(
        CancelListing command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var listing = await db.FindListingInGuildAsync(command.GuildId, command.ListingId, ct);
        if (listing is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), listing, ShopPermission.CancelListing, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.CancelListingAsync(listing, command.ActorId, command.Reason, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class FeatureListingHandler
{
    public static async Task<Result> Handle(
        FeatureListing command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var listing = await db.FindListingInGuildAsync(command.GuildId, command.ListingId, ct);
        if (listing is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        // Featuring is a seller action — owner + ShopCreator (or a shop manager). Reuses the EditListing gate.
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), listing, ShopPermission.EditListing, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.FeatureListingAsync(listing, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class ResyncShopChannelHandler
{
    public static async Task<Result> Handle(
        ResyncShopChannel command, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        // Any store owner / ShopCreator (or manager) may refresh the channel cards — it's idempotent.
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), command.GuildId, ShopPermission.ManageStore, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        await shop.ResyncShopChannelAsync(command.GuildId, ct);
        return Result.Success();
    }
}

public static class ResyncShopStoreHandler
{
    public static async Task<Result> Handle(
        ResyncShopStore command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var store = await db.FindStoreInGuildAsync(command.GuildId, command.StoreId, ct);
        if (store is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        // The store's owner (+ ShopCreator) or a manager may resync just their shop.
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), store, ShopPermission.ManageStore, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.ResyncStoreAsync(command.GuildId, command.StoreId, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class UnfeatureListingHandler
{
    public static async Task<Result> Handle(
        UnfeatureListing command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var listing = await db.FindListingInGuildAsync(command.GuildId, command.ListingId, ct);
        if (listing is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), listing, ShopPermission.EditListing, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.UnfeatureListingAsync(listing, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class PurchaseListingHandler
{
    public static async Task<Result<Guid>> Handle(
        PurchaseListing command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var listing = await db.FindListingInGuildAsync(command.GuildId, command.ListingId, ct);
        if (listing is null)
        {
            return Result<Guid>.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), listing, ShopPermission.Purchase, ct))
        {
            return Result<Guid>.Fail(nameof(ShopResult.Forbidden));
        }

        var (result, orderId) = await shop.PurchaseAsync(listing, command.ActorId, command.Quantity, ct);
        return result == ShopResult.Ok ? Result<Guid>.Success(orderId!.Value) : Result<Guid>.Fail(result.ToString());
    }
}

public static class MarkDeliveredHandler
{
    public static async Task<Result> Handle(
        MarkDelivered command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), order, ShopPermission.MarkDelivered, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.MarkDeliveredAsync(order, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class ConfirmReceiptHandler
{
    public static async Task<Result> Handle(
        ConfirmReceipt command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), order, ShopPermission.Confirm, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.ConfirmReceiptAsync(order, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class SellerCancelOrderHandler
{
    public static async Task<Result> Handle(
        SellerCancelOrder command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        // Seller-cancel reuses the CancelListing permission on the order (seller or shop manager).
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), order, ShopPermission.CancelListing, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.SellerCancelOrderAsync(order, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class MakeOfferHandler
{
    public static async Task<Result<Guid>> Handle(
        MakeOffer command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var listing = await db.FindListingInGuildAsync(command.GuildId, command.ListingId, ct);
        if (listing is null)
        {
            return Result<Guid>.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), listing, ShopPermission.MakeOffer, ct))
        {
            return Result<Guid>.Fail(nameof(ShopResult.Forbidden));
        }

        var (result, orderId) = await shop.MakeOfferAsync(listing, command.ActorId, command.Amount, command.Quantity, ct);
        return result == ShopResult.Ok ? Result<Guid>.Success(orderId!.Value) : Result<Guid>.Fail(result.ToString());
    }
}

// Accept / counter / decline / withdraw are turn-based: the service authorizes the actor against whose turn it is
// (and the manager override), so these handlers just load the order and delegate.
public static class AcceptOfferHandler
{
    public static async Task<Result> Handle(AcceptOffer command, MusterDbContext db, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        var result = await shop.AcceptOfferAsync(order, command.ActorId, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class CounterOfferHandler
{
    public static async Task<Result> Handle(CounterOffer command, MusterDbContext db, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        var result = await shop.CounterOfferAsync(order, command.ActorId, command.Amount, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class DeclineOfferHandler
{
    public static async Task<Result> Handle(DeclineOffer command, MusterDbContext db, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        var result = await shop.DeclineOfferAsync(order, command.ActorId, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class WithdrawOfferHandler
{
    public static async Task<Result> Handle(WithdrawOffer command, MusterDbContext db, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        var result = await shop.DeclineOfferAsync(order, command.ActorId, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class DisputeOrderHandler
{
    public static async Task<Result> Handle(
        DisputeOrder command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), order, ShopPermission.Dispute, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.DisputeOrderAsync(order, command.ActorId, command.Reason, command.EvidenceKey, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class ArbitrateOrderHandler
{
    public static async Task<Result> Handle(
        ArbitrateOrder command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), order, ShopPermission.Arbitrate, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.ArbitrateOrderAsync(order, command.ActorId, command.PaySeller, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class RateOrderHandler
{
    public static async Task<Result> Handle(
        RateOrder command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        var order = await db.FindOrderInGuildAsync(command.GuildId, command.OrderId, ct);
        if (order is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), order, ShopPermission.Rate, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var result = await shop.SubmitRatingAsync(order, command.ActorId, command.Stars, command.Comment, ct);
        return result == ShopResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class ModerateRatingHandler
{
    public static async Task<Result> Handle(
        ModerateRating command, MusterDbContext db, IShopAuthorizer authorizer, IShopService shop, CancellationToken ct)
    {
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), command.GuildId, ShopPermission.ModerateRating, ct))
        {
            return Result.Fail(nameof(ShopResult.Forbidden));
        }

        var rating = await db.FindRatingByIdAsync(command.GuildId, command.RatingId, ct);
        if (rating is null)
        {
            return Result.Fail(nameof(ShopResult.NotFound));
        }

        await shop.ModerateRatingAsync(rating, command.Hidden, ct);
        return Result.Success();
    }
}
