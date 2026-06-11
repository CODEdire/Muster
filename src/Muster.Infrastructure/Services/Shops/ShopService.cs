using System.Text;
using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Entities.Ratings;
using Muster.Domain.Entities.Shops;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Ratings;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Wolverine;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>
/// The shop / player-marketplace service (Phase 1: stores, categories, listing CRUD). Validates against the
/// guild's <see cref="GuildShopSettings"/> guardrails (caps, price floor/ceiling, spendable + allowed currency,
/// cooldown), mutates the domain, and publishes <c>ShopLifecycleNotified</c> on listing transitions.
/// </summary>
// Public (not internal) so Wolverine's generated handler code can inline-construct it. Adapters depend on IShopService.
public sealed class ShopService(
    MusterDbContext db,
    GuildShopSettingsService settings,
    IShopImageService images,
    Muster.Infrastructure.Services.Currencies.ICurrencyService currency,
    Muster.Infrastructure.Services.Membership.GuildAuthorizationService roles,
    IRatingService ratings,
    AuditService audit,
    IMessageBus bus) : IShopService
{
    private static string OrderKey(Guid orderId) => $"shop:order:{orderId}";

    /// <summary>Escrow key for an offer's current negotiation round — distinct per round so repeated hold/refund
    /// cycles on the same order don't collide on the ledger's (source, id) idempotency.</summary>
    private static string OfferKey(ShopOrder order) => $"{OrderKey(order.Id)}:r{order.OfferRound}";

    /// <summary>Best-effort delete of blob images by key (deduped; no-op for null/empty or when blob isn't wired).</summary>
    private async Task DeleteImagesAsync(IEnumerable<string?> keys, CancellationToken ct)
    {
        foreach (var key in keys.Where(k => !string.IsNullOrEmpty(k)).Distinct())
        {
            await images.DeleteAsync(key, ct);
        }
    }

    public async Task<(ShopResult Result, Guid? StoreId)> CreateStoreAsync(
        ulong guildId, ulong ownerId, string name, string? description, string? slug, Guid? storeTypeId = null,
        ShopStoreOrigin origin = ShopStoreOrigin.Member, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (ShopResult.NameRequired, null);
        }

        var cfg = await settings.GetAsync(guildId, ct);
        // The per-seller store cap is a member-store guardrail; guild stores are admin-curated and exempt.
        if (origin == ShopStoreOrigin.Member && cfg.MaxStoresPerSeller > 0
            && await db.CountStoresByOwnerAsync(guildId, ownerId, ct) >= cfg.MaxStoresPerSeller)
        {
            return (ShopResult.StoreCapReached, null);
        }

        if (storeTypeId is { } stid && stid != Guid.Empty && await db.FindStoreTypeInGuildAsync(guildId, stid, ct) is null)
        {
            return (ShopResult.NotFound, null);
        }

        var desired = string.IsNullOrWhiteSpace(slug) ? Slugify(name) : Slugify(slug);
        var unique = await UniqueSlugAsync(guildId, desired, ct);

        var store = ShopStore.Create(guildId, ownerId, name, unique, description, origin);
        if (storeTypeId is { } t && t != Guid.Empty) { store.StoreTypeId = t; }
        db.ShopStores.Add(store);
        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(new ShopStoreChanged(guildId, store.Id)); // post its home card
        return (ShopResult.Ok, store.Id);
    }

    public async Task<ShopResult> EditStoreAsync(
        ShopStore store, string? name, string? description, string? bannerImageKey, string? logoImageKey,
        string? accentColor, bool? closed, Guid? storeTypeId, ShopStoreOrigin? origin = null, CancellationToken ct = default)
    {
        // Convert member ↔ guild. Future sales follow the new origin (guild → burn; member → pay the owner-seller);
        // in-flight orders keep the origin they snapshotted at purchase.
        if (origin is { } o)
        {
            store.Origin = o;
        }

        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return ShopResult.InvalidState;
            }

            store.Name = name.Trim();
        }

        if (description is not null)
        {
            store.Description = description.Trim();
        }

        var replaced = new List<string?>();
        if (bannerImageKey is not null)
        {
            var next = bannerImageKey.Length == 0 ? null : bannerImageKey;
            if (store.BannerImageKey != next) { replaced.Add(store.BannerImageKey); }
            store.BannerImageKey = next;
        }

        if (logoImageKey is not null)
        {
            var next = logoImageKey.Length == 0 ? null : logoImageKey;
            if (store.LogoImageKey != next) { replaced.Add(store.LogoImageKey); }
            store.LogoImageKey = next;
        }

        if (accentColor is not null)
        {
            store.AccentColor = accentColor.Length == 0 ? null : accentColor.Trim();
        }

        if (closed is { } c)
        {
            store.Closed = c;
        }

        // Store type: null = leave unchanged; Guid.Empty = clear; a real id = set (must exist in the guild).
        if (storeTypeId is { } t)
        {
            if (t == Guid.Empty)
            {
                store.StoreTypeId = null;
            }
            else if (await db.FindStoreTypeInGuildAsync(store.GuildId, t, ct) is null)
            {
                return ShopResult.NotFound;
            }
            else
            {
                store.StoreTypeId = t;
            }
        }

        await db.SaveChangesAsync(ct);

        // Free replaced blobs, never one the store still references.
        await DeleteImagesAsync(replaced.Where(k => k != store.BannerImageKey && k != store.LogoImageKey), ct);
        await bus.PublishAsync(new ShopStoreChanged(store.GuildId, store.Id)); // refresh/remove its home card
        return ShopResult.Ok;
    }

    public async Task<ShopResult> DeleteStoreAsync(ShopStore store, CancellationToken ct = default)
    {
        // Remove the store's listings explicitly (relational cascade would handle this too, but being explicit
        // keeps the in-memory provider used by tests in lock-step). Orders/offers checks land with those phases.
        var listings = await db.ShopListings.Where(l => l.StoreId == store.Id).ToListAsync(ct);
        db.ShopListings.RemoveRange(listings);
        db.ShopStores.Remove(store);
        await db.SaveChangesAsync(ct);

        // Reclaim blob storage for every image the store + its listings owned.
        await DeleteImagesAsync(
            listings.SelectMany(l => new[] { l.ImageKey, l.ThumbKey })
                .Append(store.BannerImageKey).Append(store.LogoImageKey), ct);
        await bus.PublishAsync(new ShopStoreChanged(store.GuildId, store.Id)); // remove its home card
        return ShopResult.Ok;
    }

    public async Task<(ShopResult Result, Guid? CategoryId)> CreateCategoryAsync(
        ulong guildId, string name, int sort, int? commissionBpsOverride, string? icon, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (ShopResult.InvalidState, null);
        }

        var category = ShopCategory.Create(guildId, name, sort, commissionBpsOverride, NullIfBlank(icon));
        db.ShopCategories.Add(category);
        await db.SaveChangesAsync(ct);
        return (ShopResult.Ok, category.Id);
    }

    public async Task<ShopResult> EditCategoryAsync(ShopCategory category, string name, int sort, int? commissionBpsOverride, string? icon, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ShopResult.InvalidState;
        }

        category.Name = name.Trim();
        category.Sort = sort;
        category.CommissionBpsOverride = commissionBpsOverride;
        category.Icon = NullIfBlank(icon);
        await db.SaveChangesAsync(ct);
        return ShopResult.Ok;
    }

    public async Task<ShopResult> DeleteCategoryAsync(ShopCategory category, CancellationToken ct = default)
    {
        // Detach listings from the category (CategoryId has no FK cascade — null it out so none dangle).
        var listings = await db.ShopListings.Where(l => l.CategoryId == category.Id).ToListAsync(ct);
        foreach (var l in listings)
        {
            l.CategoryId = null;
        }

        db.ShopCategories.Remove(category);
        await db.SaveChangesAsync(ct);
        return ShopResult.Ok;
    }

    public async Task<(ShopResult Result, Guid? StoreTypeId)> CreateStoreTypeAsync(
        ulong guildId, string name, int sort, string? icon, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (ShopResult.InvalidState, null);
        }

        var type = ShopStoreType.Create(guildId, name, sort, NullIfBlank(icon));
        db.ShopStoreTypes.Add(type);
        await db.SaveChangesAsync(ct);
        return (ShopResult.Ok, type.Id);
    }

    public async Task<ShopResult> EditStoreTypeAsync(ShopStoreType type, string name, int sort, string? icon, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ShopResult.InvalidState;
        }

        type.Name = name.Trim();
        type.Sort = sort;
        type.Icon = NullIfBlank(icon);
        await db.SaveChangesAsync(ct);

        // A renamed/re-iconed type changes the hero cards of stores using it.
        foreach (var s in await db.ShopStores.Where(st => st.StoreTypeId == type.Id).Select(st => new { st.GuildId, st.Id }).ToListAsync(ct))
        {
            await bus.PublishAsync(new ShopStoreChanged(s.GuildId, s.Id));
        }

        return ShopResult.Ok;
    }

    public async Task<ShopResult> DeleteStoreTypeAsync(ShopStoreType type, CancellationToken ct = default)
    {
        // Detach stores from the type (no FK cascade — null it so none dangle), then refresh their hero cards.
        var stores = await db.ShopStores.Where(s => s.StoreTypeId == type.Id).ToListAsync(ct);
        foreach (var s in stores)
        {
            s.StoreTypeId = null;
        }

        db.ShopStoreTypes.Remove(type);
        await db.SaveChangesAsync(ct);

        foreach (var s in stores)
        {
            await bus.PublishAsync(new ShopStoreChanged(s.GuildId, s.Id));
        }

        return ShopResult.Ok;
    }

    public async Task<(ShopResult Result, Guid? ListingId)> PostListingAsync(
        ShopStore store, ulong sellerId, string name, string currencyCode, long price, string? description,
        Guid? categoryId, int quantity, bool acceptsOffers, string? imageKey, string? thumbKey, DateTimeOffset? expiresAt,
        IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        if (store.Closed)
        {
            return (ShopResult.NotActive, null);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return (ShopResult.NameRequired, null);
        }

        if (price <= 0)
        {
            return (ShopResult.InvalidPrice, null);
        }

        var cfg = await settings.GetAsync(store.GuildId, ct);

        // Currency must exist, be spendable, and (if a guild allow-list is set) be on it.
        var currency = await db.FindCurrencyAsync(store.GuildId, currencyCode, ct);
        if (currency is null || !currency.IsSpendable)
        {
            return (ShopResult.NotSpendable, null);
        }

        if (cfg.AllowedCurrencyIds.Count > 0 && !cfg.AllowedCurrencyIds.Contains(currency.Id))
        {
            return (ShopResult.NotSpendable, null);
        }

        // Price floor / ceiling.
        if (cfg.MinPrice > 0 && price < cfg.MinPrice)
        {
            return (ShopResult.BelowFloor, null);
        }

        if (cfg.MaxPrice > 0 && price > cfg.MaxPrice)
        {
            return (ShopResult.AboveCeiling, null);
        }

        // Category required / valid.
        if (cfg.RequireCategory && categoryId is null)
        {
            return (ShopResult.CategoryRequired, null);
        }

        if (categoryId is { } cid && await db.FindCategoryInGuildAsync(store.GuildId, cid, ct) is null)
        {
            return (ShopResult.NotFound, null);
        }

        // Per-seller active-listing cap.
        if (cfg.MaxActiveListingsPerSeller > 0
            && await db.CountActiveListingsBySellerAsync(store.GuildId, sellerId, ct) >= cfg.MaxActiveListingsPerSeller)
        {
            return (ShopResult.ListingCapReached, null);
        }

        // Anti-spam cooldown between a seller's listings.
        if (cfg.ListingCooldownMinutes > 0
            && await db.LatestListingAtBySellerAsync(store.GuildId, sellerId, ct) is { } last
            && last.AddMinutes(cfg.ListingCooldownMinutes) > DateTimeOffset.UtcNow)
        {
            return (ShopResult.Cooldown, null);
        }

        var effectiveTags = cfg.PlayerTagsEnabled ? CapTags(tags, cfg.MaxTagsPerListing) : null;
        var deadline = expiresAt ?? (cfg.ListingDefaultExpiryDays > 0
            ? DateTimeOffset.UtcNow.AddDays(cfg.ListingDefaultExpiryDays)
            : null);

        var listing = ShopListing.Create(new ShopListingDraft(
            store.GuildId, store.Id, sellerId, name, description ?? string.Empty, currency.Id, price,
            CategoryId: categoryId, Quantity: quantity, AcceptsOffers: acceptsOffers,
            ImageKey: imageKey, ThumbKey: thumbKey, ExpiresAt: deadline, Tags: effectiveTags));

        db.ShopListings.Add(listing);
        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new ShopLifecycleNotified(
            store.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.Listed, null,
            $"New listing in {store.Name}"));

        return (ShopResult.Ok, listing.Id);
    }

    public async Task<ShopResult> EditListingAsync(
        ShopListing listing, string? name, string? description, long? price, Guid? categoryId, int? quantity,
        bool? acceptsOffers, string? imageKey, string? thumbKey, DateTimeOffset? expiresAt, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        if (listing.Status != ShopListingStatus.Active)
        {
            return ShopResult.NotActive;
        }

        // Edit-lock: once a listing has live orders/offers, its terms (price, stock, …) are frozen so a buyer
        // mid-purchase or mid-negotiation can't have the deal changed under them. Withdraw + relist to change it.
        if (await db.CountActiveOrdersForListingAsync(listing.Id, ct) > 0)
        {
            return ShopResult.HasOrders;
        }

        var cfg = await settings.GetAsync(listing.GuildId, ct);

        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return ShopResult.NameRequired;
            }

            listing.Name = name.Trim();
        }

        if (description is not null)
        {
            listing.Description = description.Trim();
        }

        if (price is { } p)
        {
            if (p <= 0)
            {
                return ShopResult.InvalidPrice;
            }

            if (cfg.MinPrice > 0 && p < cfg.MinPrice)
            {
                return ShopResult.BelowFloor;
            }

            if (cfg.MaxPrice > 0 && p > cfg.MaxPrice)
            {
                return ShopResult.AboveCeiling;
            }

            listing.Price = p;
        }

        if (categoryId is { } cid && await db.FindCategoryInGuildAsync(listing.GuildId, cid, ct) is null)
        {
            return ShopResult.NotFound;
        }

        if (categoryId is not null)
        {
            listing.CategoryId = categoryId;
        }

        if (quantity is { } q)
        {
            listing.Quantity = Math.Max(1, q);
        }

        if (acceptsOffers is { } ao)
        {
            listing.AcceptsOffers = ao;
        }

        var replacedImages = new List<string?>();
        if (imageKey is not null)
        {
            var next = imageKey.Length == 0 ? null : imageKey;
            if (listing.ImageKey != next)
            {
                replacedImages.Add(listing.ImageKey); // free the old blob once the change is saved
            }

            listing.ImageKey = next;
        }

        if (thumbKey is not null)
        {
            var next = thumbKey.Length == 0 ? null : thumbKey;
            if (listing.ThumbKey != next)
            {
                replacedImages.Add(listing.ThumbKey);
            }

            listing.ThumbKey = next;
        }

        if (expiresAt is not null)
        {
            listing.ExpiresAt = expiresAt;
        }

        if (tags is not null && cfg.PlayerTagsEnabled)
        {
            listing.Tags.Clear();
            foreach (var t in ShopListingTag.Normalize(CapTags(tags, cfg.MaxTagsPerListing)))
            {
                listing.Tags.Add(new ShopListingTag { ListingId = listing.Id, Tag = t });
            }
        }

        await db.SaveChangesAsync(ct);

        // Free any replaced blobs, but never a key the listing still references (thumb == image in v1).
        await DeleteImagesAsync(
            replacedImages.Where(k => k != listing.ImageKey && k != listing.ThumbKey), ct);

        // Refresh any board card (a featured listing's price/name may have changed). No-op for non-featured.
        await bus.PublishAsync(new ShopLifecycleNotified(
            listing.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.Listed, null, "Listing updated"));
        return ShopResult.Ok;
    }

    public async Task<ShopResult> CancelListingAsync(ShopListing listing, ulong actorId, string? reason, CancellationToken ct = default)
    {
        if (listing.Status is ShopListingStatus.Cancelled or ShopListingStatus.Expired)
        {
            return ShopResult.NotActive;
        }

        var (oldImage, oldThumb) = (listing.ImageKey, listing.ThumbKey);
        var moderated = actorId != listing.SellerId; // a non-seller delisting = a moderator takedown
        listing.DelistedBy = actorId;
        listing.DelistReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        listing.FeaturedUntil = null; // delisted → drop any featured card + free the slot
        listing.TransitionTo(ShopListingStatus.Cancelled);
        await db.SaveChangesAsync(ct);

        await DeleteImagesAsync([oldImage, oldThumb], ct);

        // DM the seller on a takedown (closing the loop with the reason); a self-withdrawal needs no notice.
        var detail = moderated
            ? "Taken down by a moderator" + (listing.DelistReason is { } r ? $": {r}" : ".")
            : "Listing withdrawn";
        await bus.PublishAsync(new ShopLifecycleNotified(
            listing.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.Cancelled,
            moderated ? listing.SellerId : null, detail));

        return ShopResult.Ok;
    }

    public async Task<ShopResult> FeatureListingAsync(ShopListing listing, CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync(listing.GuildId, ct);
        if (!cfg.PlayerMarketEnabled || cfg.MaxFeaturedPerStore <= 0)
        {
            return ShopResult.NotActive;
        }

        if (listing.Status != ShopListingStatus.Active)
        {
            return ShopResult.NotActive;
        }

        var now = DateTimeOffset.UtcNow;
        if (listing.FeaturedUntil is { } current && current > now)
        {
            return ShopResult.InvalidState; // already featured
        }

        if (await db.CountFeaturedInStoreAsync(listing.StoreId, now, ct) >= cfg.MaxFeaturedPerStore)
        {
            return ShopResult.FeaturedCapReached;
        }

        var hours = cfg.FeaturedDurationHours > 0 ? cfg.FeaturedDurationHours : 72;
        var until = now.AddHours(hours);

        if (cfg.FeaturedListingFee > 0)
        {
            // Burn the fee from the seller in the listing's own currency (idempotent per feature window).
            var burn = await currency.ShopBurnAsync(
                listing.GuildId, listing.SellerId, listing.CurrencyId, cfg.FeaturedListingFee, $"shop:feature:{listing.Id}:{until:O}", ct);
            if (burn != Currencies.EscrowStatus.Ok)
            {
                return ToShopResult(burn);
            }
        }

        listing.FeaturedUntil = until;
        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new ShopLifecycleNotified(
            listing.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.Featured, null, "Listing featured"));

        return ShopResult.Ok;
    }

    public async Task<ShopResult> UnfeatureListingAsync(ShopListing listing, CancellationToken ct = default)
    {
        if (listing.FeaturedUntil is not { } until || until <= DateTimeOffset.UtcNow)
        {
            return ShopResult.InvalidState; // not currently featured
        }

        listing.FeaturedUntil = null; // free the slot; fee is NOT refunded
        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new ShopLifecycleNotified(
            listing.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.Unfeatured, null, "Listing un-featured"));
        return ShopResult.Ok;
    }

    public async Task<int> ExpireFeaturedDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var due = await db.ListExpiredFeaturedAsync(now, ct);
        foreach (var listing in due)
        {
            listing.FeaturedUntil = null;
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var listing in due)
            {
                await bus.PublishAsync(new ShopLifecycleNotified(
                    listing.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.Unfeatured, null, "Feature ended"));
            }
        }

        return due.Count;
    }

    public async Task<int> ResyncShopChannelAsync(ulong guildId, CancellationToken ct = default)
    {
        // (Re)post every open store's home card + every currently-featured listing's card. Covers "channel linked
        // after the fact" and channel moves; the bot handlers are idempotent (edit/move/skip).
        var stores = await db.ListActiveStoresAsync(guildId, ct);
        foreach (var store in stores)
        {
            await bus.PublishAsync(new ShopStoreChanged(guildId, store.Id));
        }

        await PublishFeaturedAsync(await db.ListActiveFeaturedAsync(guildId, DateTimeOffset.UtcNow, ct));
        return stores.Count;
    }

    public async Task<ShopResult> ResyncStoreAsync(ulong guildId, Guid storeId, CancellationToken ct = default)
    {
        var store = await db.FindStoreInGuildAsync(guildId, storeId, ct);
        if (store is null)
        {
            return ShopResult.NotFound;
        }

        await bus.PublishAsync(new ShopStoreChanged(guildId, storeId));
        await PublishFeaturedAsync(
            (await db.ListActiveFeaturedAsync(guildId, DateTimeOffset.UtcNow, ct)).Where(l => l.StoreId == storeId).ToList());
        return ShopResult.Ok;
    }

    private async Task PublishFeaturedAsync(IReadOnlyList<ShopListing> featured)
    {
        foreach (var listing in featured)
        {
            await bus.PublishAsync(new ShopLifecycleNotified(
                listing.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.Featured, null, "Featured card resync"));
        }
    }

    public async Task<int> ExpireListingsDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var due = await db.ListExpiredListingsAsync(now, ct);
        foreach (var listing in due)
        {
            listing.FeaturedUntil = null; // a delisted item can't stay featured
            listing.TransitionTo(ShopListingStatus.Expired);
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var listing in due)
            {
                await bus.PublishAsync(new ShopLifecycleNotified(
                    listing.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.Expired, listing.SellerId, "Listing expired"));
            }
        }

        return due.Count;
    }

    // --- Orders (buy-now escrow) ---

    public async Task<(ShopResult Result, Guid? OrderId)> PurchaseAsync(ShopListing listing, ulong buyerId, int quantity, CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync(listing.GuildId, ct);
        if (!cfg.PlayerMarketEnabled)
        {
            return (ShopResult.NotActive, null);
        }

        if (listing.Status != ShopListingStatus.Active || listing.Store?.Closed == true)
        {
            return (ShopResult.NotActive, null);
        }

        // A seller buying their own listing is allowed (e.g. to clear stock) — the commission still burns.
        var qty = Math.Max(1, quantity);
        if (listing.Quantity < qty)
        {
            return (ShopResult.OutOfStock, null);
        }

        var amount = listing.Price * qty;
        var order = ShopOrder.Create(listing, buyerId, qty, amount);

        // Reserve the buyer's funds; only persist the order if the hold succeeds (atomic).
        var hold = await currency.ShopHoldAsync(listing.GuildId, buyerId, listing.CurrencyId, amount, OrderKey(order.Id), ct);
        if (hold != Currencies.EscrowStatus.Ok)
        {
            return (ToShopResult(hold), null);
        }

        db.ShopOrders.Add(order);
        listing.Quantity -= qty;
        if (listing.Quantity <= 0)
        {
            listing.TransitionTo(ShopListingStatus.SoldOut);
            listing.FeaturedUntil = null; // sold out → free the featured slot + drop the card
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two buyers raced the last unit(s): the RowVersion check lost. Nothing committed (incl. the hold),
            // so the order simply didn't happen — tell this buyer it's out of stock.
            return (ShopResult.OutOfStock, null);
        }

        await bus.PublishAsync(new ShopLifecycleNotified(
            listing.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.Purchased, listing.SellerId,
            "Item purchased — awaiting delivery confirmation", order.Id));

        return (ShopResult.Ok, order.Id);
    }

    public async Task<ShopResult> MarkDeliveredAsync(ShopOrder order, CancellationToken ct = default)
    {
        if (order.Status != ShopOrderStatus.PendingDelivery)
        {
            return ShopResult.InvalidState;
        }

        order.DeliveredAt = DateTimeOffset.UtcNow;
        order.TransitionTo(ShopOrderStatus.Delivered); // bumps StatusChangedAt → buyer-confirm clock starts now
        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new ShopLifecycleNotified(
            order.GuildId, order.ListingId, order.ItemNameSnapshot, ShopLifecycleMoment.Delivered, order.BuyerId,
            "Marked delivered — please confirm receipt", order.Id));

        return ShopResult.Ok;
    }

    public async Task<ShopResult> ConfirmReceiptAsync(ShopOrder order, CancellationToken ct = default)
    {
        if (order.Status is not (ShopOrderStatus.PendingDelivery or ShopOrderStatus.Delivered))
        {
            return ShopResult.InvalidState;
        }

        var cfg = await settings.GetAsync(order.GuildId, ct);

        // Under two-step delivery the buyer can only confirm once the seller has marked it delivered.
        if (cfg.TwoStepDelivery && order.Status == ShopOrderStatus.PendingDelivery)
        {
            return ShopResult.NotDelivered;
        }

        // Guild store → consume (burn the buyer's payment, no seller, no commission); member store → pay the seller.
        var isGuild = order.Origin == ShopStoreOrigin.Guild;
        var fee = isGuild ? 0 : await FeeForAsync(order, cfg, ct);
        var settle = isGuild
            ? await currency.ShopConsumeAsync(order.GuildId, order.CurrencyId, order.Amount, OrderKey(order.Id), ct)
            : await currency.ShopSettleAsync(order.GuildId, order.SellerId, order.CurrencyId, order.Amount, fee, OrderKey(order.Id), ct);
        if (settle != Currencies.EscrowStatus.Ok)
        {
            return ToShopResult(settle);
        }

        var now = DateTimeOffset.UtcNow;
        order.FeeAmount = fee;
        order.ConfirmedAt = now;
        order.SettledAt = now;
        order.RatingWindowClosesAt = !isGuild && cfg.RatingWindowHours > 0 ? now.AddHours(cfg.RatingWindowHours) : null;
        order.TransitionTo(ShopOrderStatus.Settled);
        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new ShopLifecycleNotified(
            order.GuildId, order.ListingId, order.ItemNameSnapshot, ShopLifecycleMoment.Settled, order.SellerId,
            isGuild ? "Order settled — payment consumed by the guild" : "Order settled — funds released to seller", order.Id));

        return ShopResult.Ok;
    }

    public async Task<ShopResult> SellerCancelOrderAsync(ShopOrder order, CancellationToken ct = default)
    {
        if (order.Status is not (ShopOrderStatus.PendingDelivery or ShopOrderStatus.Delivered))
        {
            return ShopResult.InvalidState;
        }

        var refund = await currency.ShopRefundAsync(order.GuildId, order.BuyerId, order.CurrencyId, order.Amount, OrderKey(order.Id), ct);
        if (refund != Currencies.EscrowStatus.Ok)
        {
            return ToShopResult(refund);
        }

        order.TransitionTo(ShopOrderStatus.Cancelled);

        // Release the reserved stock; reopen the listing if it had sold out (and isn't otherwise terminal).
        if (order.Listing is { } l)
        {
            l.Quantity += order.Quantity;
            if (l.Status == ShopListingStatus.SoldOut)
            {
                l.TransitionTo(ShopListingStatus.Active);
            }
        }

        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new ShopLifecycleNotified(
            order.GuildId, order.ListingId, order.ItemNameSnapshot, ShopLifecycleMoment.Cancelled, order.BuyerId,
            "Order cancelled — refunded", order.Id));

        return ShopResult.Ok;
    }

    public async Task<int> AutoSettleDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var pending = await db.ListAwaitingConfirmationAsync(ct);
        var settled = 0;
        foreach (var byGuild in pending.GroupBy(o => o.GuildId))
        {
            var cfg = await settings.GetAsync(byGuild.Key, ct);
            if (cfg.DeliveryConfirmTimeoutHours <= 0)
            {
                continue; // auto-settle disabled for this guild
            }

            var cutoff = now.AddHours(-cfg.DeliveryConfirmTimeoutHours);
            foreach (var order in byGuild.Where(o => o.StatusChangedAt < cutoff))
            {
                // Two-step delivery: the confirm clock only runs once the seller has marked the order delivered,
                // so a not-yet-delivered order never auto-settles. One-step settles from purchase time.
                if (cfg.TwoStepDelivery && order.Status != ShopOrderStatus.Delivered)
                {
                    continue;
                }

                if (await ConfirmReceiptAsync(order, ct) == ShopResult.Ok)
                {
                    await audit.RecordAsync(order.GuildId, 0, AuditActions.Shop.AutoSettle, $"order:{order.Id} auto-settled to seller", targetUserId: order.SellerId, ct: ct);
                    settled++;
                }
            }
        }

        return settled;
    }

    /// <summary>Two-step delivery: cancel + refund orders the seller never marked delivered within the guild's
    /// undelivered window, so a buyer isn't stuck holding escrow against a seller who vanished.</summary>
    public async Task<int> AutoCancelUndeliveredDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var pending = await db.ListAwaitingConfirmationAsync(ct);
        var cancelled = 0;
        foreach (var byGuild in pending.GroupBy(o => o.GuildId))
        {
            var cfg = await settings.GetAsync(byGuild.Key, ct);
            if (!cfg.TwoStepDelivery || cfg.UndeliveredTimeoutHours <= 0)
            {
                continue; // only meaningful under two-step, and when the guild enabled the window
            }

            var cutoff = now.AddHours(-cfg.UndeliveredTimeoutHours);
            foreach (var order in byGuild.Where(o => o.Status == ShopOrderStatus.PendingDelivery && o.StatusChangedAt < cutoff))
            {
                if (await SellerCancelOrderAsync(order, ct) == ShopResult.Ok)
                {
                    await audit.RecordAsync(order.GuildId, 0, AuditActions.Shop.AutoCancelUndelivered, $"order:{order.Id} never delivered — refunded buyer", targetUserId: order.BuyerId, ct: ct);
                    cancelled++;
                }
            }
        }

        return cancelled;
    }

    public async Task<ShopResult> DisputeOrderAsync(ShopOrder order, ulong disputedBy, string reason, string? evidenceKey, CancellationToken ct = default)
    {
        if (order.Status is not (ShopOrderStatus.PendingDelivery or ShopOrderStatus.Delivered))
        {
            return ShopResult.InvalidState;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ShopResult.InvalidState;
        }

        order.DisputedBy = disputedBy;
        order.DisputeReason = reason.Trim();
        order.DisputeEvidenceKey = string.IsNullOrEmpty(evidenceKey) ? null : evidenceKey;
        order.TransitionTo(ShopOrderStatus.Disputed);
        await db.SaveChangesAsync(ct);

        // null target → the shop's mod channel / managers (a bot consumer routes Disputed to ShopModChannelId).
        await bus.PublishAsync(new ShopLifecycleNotified(
            order.GuildId, order.ListingId, order.ItemNameSnapshot, ShopLifecycleMoment.Disputed, null,
            $"Dispute raised: {order.DisputeReason}", order.Id));

        return ShopResult.Ok;
    }

    public async Task<ShopResult> ArbitrateOrderAsync(ShopOrder order, ulong resolverId, bool paySeller, CancellationToken ct = default)
    {
        if (order.Status != ShopOrderStatus.Disputed)
        {
            return ShopResult.InvalidState;
        }

        var cfg = await settings.GetAsync(order.GuildId, ct);
        var result = await ResolveAsync(order, cfg, paySeller, resolverId, ct);
        if (result != ShopResult.Ok)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(new ShopLifecycleNotified(
            order.GuildId, order.ListingId, order.ItemNameSnapshot, ShopLifecycleMoment.Arbitrated,
            paySeller ? order.SellerId : order.BuyerId,
            paySeller ? "Dispute resolved — paid the seller" : "Dispute resolved — refunded the buyer", order.Id));

        return ShopResult.Ok;
    }

    public async Task<int> AutoResolveDisputesAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var disputed = await db.ListDisputedAsync(ct);
        var resolved = 0;
        foreach (var byGuild in disputed.GroupBy(o => o.GuildId))
        {
            var cfg = await settings.GetAsync(byGuild.Key, ct);
            if (cfg.DisputeTimeoutHours <= 0)
            {
                continue;
            }

            var cutoff = now.AddHours(-cfg.DisputeTimeoutHours);
            foreach (var order in byGuild.Where(o => o.StatusChangedAt < cutoff))
            {
                // Favour the non-disputing party: raising a dispute and letting it lapse shouldn't win.
                var paySeller = order.DisputedBy == order.BuyerId;
                if (await ResolveAsync(order, cfg, paySeller, resolverId: null, ct) == ShopResult.Ok)
                {
                    await db.SaveChangesAsync(ct);
                    await audit.RecordAsync(order.GuildId, 0, AuditActions.Shop.AutoResolveDispute, $"order:{order.Id} timed out → {(paySeller ? "paid seller" : "refunded buyer")}", targetUserId: paySeller ? order.SellerId : order.BuyerId, ct: ct);
                    resolved++;
                }
            }
        }

        return resolved;
    }

    // --- Offers (binding hold, seller approves) ---

    public async Task<(ShopResult Result, Guid? OrderId)> MakeOfferAsync(ShopListing listing, ulong buyerId, long amount, int quantity, CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync(listing.GuildId, ct);
        if (!cfg.PlayerMarketEnabled || !cfg.OffersEnabled || !listing.AcceptsOffers)
        {
            return (ShopResult.NotActive, null);
        }

        if (listing.Status != ShopListingStatus.Active || listing.Store?.Closed == true)
        {
            return (ShopResult.NotActive, null);
        }

        // Guild stores are buy-now only — no negotiation (there's no member seller to counter).
        if (listing.Store?.Origin == ShopStoreOrigin.Guild)
        {
            return (ShopResult.NotActive, null);
        }

        if (listing.SellerId == buyerId)
        {
            return (ShopResult.Forbidden, null);
        }

        if (amount <= 0)
        {
            return (ShopResult.InvalidState, null);
        }

        if (cfg.MinPrice > 0 && amount < cfg.MinPrice)
        {
            return (ShopResult.BelowFloor, null);
        }

        if (cfg.MaxOpenOffersPerBuyer > 0
            && await db.CountOpenOffersByBuyerAsync(listing.GuildId, buyerId, ct) >= cfg.MaxOpenOffersPerBuyer)
        {
            return (ShopResult.OfferCapReached, null);
        }

        var qty = Math.Max(1, quantity);
        var order = ShopOrder.CreateOffer(listing, buyerId, qty, amount);

        // Binding: hold the buyer's offered amount now; the seller accepting settles from it, declining refunds it.
        var hold = await currency.ShopHoldAsync(listing.GuildId, buyerId, listing.CurrencyId, amount, OfferKey(order), ct);
        if (hold != Currencies.EscrowStatus.Ok)
        {
            return (ToShopResult(hold), null);
        }

        db.ShopOrders.Add(order);
        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new ShopLifecycleNotified(
            listing.GuildId, listing.Id, listing.Name, ShopLifecycleMoment.OfferMade, listing.SellerId,
            $"Offer of {amount}", order.Id));

        return (ShopResult.Ok, order.Id);
    }

    public async Task<ShopResult> AcceptOfferAsync(ShopOrder order, ulong actorId, CancellationToken ct = default)
    {
        if (order.Status != ShopOrderStatus.OfferPending)
        {
            return ShopResult.InvalidState;
        }

        // The party whose turn it is accepts the current price. Buyer-proposed → seller's (or manager's) turn;
        // seller-proposed (a counter) → buyer's turn.
        var isManager = await roles.IsShopManagerAsync(order.GuildId, actorId, ct);
        var buyerTurn = order.OfferProposedBy == ShopOfferParty.Seller;
        var allowed = buyerTurn ? actorId == order.BuyerId : (actorId == order.SellerId || isManager);
        if (!allowed)
        {
            return ShopResult.Forbidden;
        }

        var listing = order.Listing;
        if (listing is null || listing.Status != ShopListingStatus.Active)
        {
            return ShopResult.NotActive;
        }

        if (listing.Quantity < order.Quantity)
        {
            return ShopResult.OutOfStock;
        }

        // Buyer accepting a seller counter holds the funds now (binding); a buyer-proposed offer is already held.
        if (buyerTurn)
        {
            var hold = await currency.ShopHoldAsync(order.GuildId, order.BuyerId, order.CurrencyId, order.Amount, OfferKey(order), ct);
            if (hold != Currencies.EscrowStatus.Ok)
            {
                return ToShopResult(hold);
            }
        }

        order.TransitionTo(ShopOrderStatus.PendingDelivery);
        listing.Quantity -= order.Quantity;
        var soldOut = listing.Quantity <= 0;
        if (soldOut)
        {
            listing.TransitionTo(ShopListingStatus.SoldOut);
            listing.FeaturedUntil = null;
        }

        await db.SaveChangesAsync(ct);

        // A sold-out listing can't honour competing offers — decline + refund the ones currently holding funds.
        if (soldOut)
        {
            foreach (var other in await db.ListOpenOffersForListingAsync(listing.Id, order.Id, ct))
            {
                if (other.OfferProposedBy == ShopOfferParty.Buyer) // only buyer-proposed offers hold funds
                {
                    await currency.ShopRefundAsync(other.GuildId, other.BuyerId, other.CurrencyId, other.Amount, OfferKey(other), ct);
                }

                other.TransitionTo(ShopOrderStatus.OfferDeclined);
                await bus.PublishAsync(new ShopLifecycleNotified(
                    other.GuildId, other.ListingId, other.ItemNameSnapshot, ShopLifecycleMoment.OfferRejected, other.BuyerId,
                    "Offer declined — the item sold", other.Id));
            }

            await db.SaveChangesAsync(ct);
        }

        await bus.PublishAsync(new ShopLifecycleNotified(
            order.GuildId, order.ListingId, order.ItemNameSnapshot, ShopLifecycleMoment.OfferAccepted, order.BuyerId,
            "Offer accepted — awaiting delivery confirmation", order.Id));

        return ShopResult.Ok;
    }

    public async Task<ShopResult> CounterOfferAsync(ShopOrder order, ulong actorId, long amount, CancellationToken ct = default)
    {
        if (order.Status != ShopOrderStatus.OfferPending)
        {
            return ShopResult.InvalidState;
        }

        if (amount <= 0)
        {
            return ShopResult.InvalidState;
        }

        // Only the party whose turn it is may counter (managers don't negotiate prices).
        var sellerTurn = order.OfferProposedBy == ShopOfferParty.Buyer;
        var allowed = sellerTurn ? actorId == order.SellerId : actorId == order.BuyerId;
        if (!allowed)
        {
            return ShopResult.Forbidden;
        }

        var cfg = await settings.GetAsync(order.GuildId, ct);
        if (cfg.MinPrice > 0 && amount < cfg.MinPrice)
        {
            return ShopResult.BelowFloor;
        }

        if (sellerTurn)
        {
            // Seller counters: release the buyer's standing hold, then bump the round so the next hold uses a fresh
            // escrow key. The seller's price carries no hold until the buyer accepts/re-counters.
            await currency.ShopRefundAsync(order.GuildId, order.BuyerId, order.CurrencyId, order.Amount, OfferKey(order), ct);
            order.OfferRound++;
            order.OfferProposedBy = ShopOfferParty.Seller;
        }
        else
        {
            // Buyer counters back: hold the new (binding) amount at the current (already-bumped) round.
            var hold = await currency.ShopHoldAsync(order.GuildId, order.BuyerId, order.CurrencyId, amount, OfferKey(order), ct);
            if (hold != Currencies.EscrowStatus.Ok)
            {
                return ToShopResult(hold);
            }

            order.OfferProposedBy = ShopOfferParty.Buyer;
        }

        order.Amount = amount;
        order.StatusChangedAt = DateTimeOffset.UtcNow; // reset the offer-expiry clock on each turn
        await db.SaveChangesAsync(ct);

        var awaiting = order.OfferProposedBy == ShopOfferParty.Buyer ? order.SellerId : order.BuyerId;
        await bus.PublishAsync(new ShopLifecycleNotified(
            order.GuildId, order.ListingId, order.ItemNameSnapshot, ShopLifecycleMoment.OfferCountered, awaiting,
            $"Countered at {amount}", order.Id));

        return ShopResult.Ok;
    }

    public async Task<ShopResult> DeclineOfferAsync(ShopOrder order, ulong actorId, CancellationToken ct = default)
    {
        if (order.Status != ShopOrderStatus.OfferPending)
        {
            return ShopResult.InvalidState;
        }

        // Any party (buyer, seller, or a shop manager) may end the negotiation.
        var isManager = await roles.IsShopManagerAsync(order.GuildId, actorId, ct);
        if (actorId != order.BuyerId && actorId != order.SellerId && !isManager)
        {
            return ShopResult.Forbidden;
        }

        return await EndOfferAsync(order, ct);
    }

    /// <summary>End a pending offer: refund the buyer if their price is the one currently held, mark declined.
    /// No authorization — callers (decline/withdraw handlers, the expiry sweep) gate access.</summary>
    private async Task<ShopResult> EndOfferAsync(ShopOrder order, CancellationToken ct)
    {
        if (order.Status != ShopOrderStatus.OfferPending)
        {
            return ShopResult.InvalidState;
        }

        // Only a buyer-proposed price has funds held to refund; a seller's standing counter doesn't.
        if (order.OfferProposedBy == ShopOfferParty.Buyer)
        {
            var refund = await currency.ShopRefundAsync(order.GuildId, order.BuyerId, order.CurrencyId, order.Amount, OfferKey(order), ct);
            if (refund != Currencies.EscrowStatus.Ok)
            {
                return ToShopResult(refund);
            }
        }

        order.TransitionTo(ShopOrderStatus.OfferDeclined);
        await db.SaveChangesAsync(ct);

        await bus.PublishAsync(new ShopLifecycleNotified(
            order.GuildId, order.ListingId, order.ItemNameSnapshot, ShopLifecycleMoment.OfferRejected, order.BuyerId,
            "Offer ended", order.Id));

        return ShopResult.Ok;
    }

    public async Task<int> AutoExpireOffersAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var offers = await db.ListOpenOffersAsync(ct);
        var expired = 0;
        foreach (var byGuild in offers.GroupBy(o => o.GuildId))
        {
            var cfg = await settings.GetAsync(byGuild.Key, ct);
            if (cfg.OfferExpiryHours <= 0)
            {
                continue;
            }

            var cutoff = now.AddHours(-cfg.OfferExpiryHours);
            foreach (var order in byGuild.Where(o => o.StatusChangedAt < cutoff))
            {
                if (await EndOfferAsync(order, ct) == ShopResult.Ok)
                {
                    await audit.RecordAsync(order.GuildId, 0, AuditActions.Shop.OfferExpired, $"order:{order.Id} offer expired — buyer hold released", targetUserId: order.BuyerId, ct: ct);
                    expired++;
                }
            }
        }

        return expired;
    }

    /// <summary>Shared dispute/auto-resolve resolution: pay the seller (settle + burn) or refund the buyer. Stages
    /// ledger legs + flips status; the caller saves.</summary>
    private async Task<ShopResult> ResolveAsync(ShopOrder order, GuildShopSettings cfg, bool paySeller, ulong? resolverId, CancellationToken ct)
    {
        if (paySeller)
        {
            // Guild store → consume (burn); member store → pay the seller, less the burned commission.
            var isGuild = order.Origin == ShopStoreOrigin.Guild;
            var fee = isGuild ? 0 : await FeeForAsync(order, cfg, ct);
            var settle = isGuild
                ? await currency.ShopConsumeAsync(order.GuildId, order.CurrencyId, order.Amount, OrderKey(order.Id), ct)
                : await currency.ShopSettleAsync(order.GuildId, order.SellerId, order.CurrencyId, order.Amount, fee, OrderKey(order.Id), ct);
            if (settle != Currencies.EscrowStatus.Ok)
            {
                return ToShopResult(settle);
            }

            var now = DateTimeOffset.UtcNow;
            order.FeeAmount = fee;
            order.SettledAt = now;
            order.RatingWindowClosesAt = !isGuild && cfg.RatingWindowHours > 0 ? now.AddHours(cfg.RatingWindowHours) : null;
            order.ResolvedBy = resolverId;
            order.TransitionTo(ShopOrderStatus.Settled);
            return ShopResult.Ok;
        }

        var refund = await currency.ShopRefundAsync(order.GuildId, order.BuyerId, order.CurrencyId, order.Amount, OrderKey(order.Id), ct);
        if (refund != Currencies.EscrowStatus.Ok)
        {
            return ToShopResult(refund);
        }

        order.ResolvedBy = resolverId;
        // A dispute on an order the seller never delivered (two-step) resolves as a cancellation; once delivered
        // (or one-step, where delivery is out-of-band) a buyer-favourable resolution is a refund. Both return the
        // buyer's escrow with no fee.
        var neverDelivered = cfg.TwoStepDelivery && order.DeliveredAt is null;
        order.TransitionTo(neverDelivered ? ShopOrderStatus.Cancelled : ShopOrderStatus.Refunded);
        if (order.Listing is { } l)
        {
            l.Quantity += order.Quantity;
            if (l.Status == ShopListingStatus.SoldOut)
            {
                l.TransitionTo(ShopListingStatus.Active);
            }
        }

        return ShopResult.Ok;
    }

    // --- Ratings (blind-mutual) ---

    public async Task<ShopResult> SubmitRatingAsync(ShopOrder order, ulong raterId, int stars, string? comment, CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync(order.GuildId, ct);
        if (!cfg.RatingsEnabled)
        {
            return ShopResult.NotActive;
        }

        // A guild store has no member seller to rate (the guild ran the sale).
        if (order.Origin == ShopStoreOrigin.Guild)
        {
            return ShopResult.NotActive;
        }

        // Only a settled order is ratable, and only while its window is open.
        if (order.Status != ShopOrderStatus.Settled)
        {
            return ShopResult.InvalidState;
        }

        if (order.RatingsClosed || (order.RatingWindowClosesAt is { } closesAt && DateTimeOffset.UtcNow > closesAt))
        {
            return ShopResult.RatingWindowClosed;
        }

        var isBuyer = raterId == order.BuyerId;
        var isSeller = raterId == order.SellerId;
        if (!isBuyer && !isSeller)
        {
            return ShopResult.Forbidden;
        }

        // Buyer rates the seller (the Provider); seller rates the buyer (the Consumer).
        var subjectId = isBuyer ? order.SellerId : order.BuyerId;
        var role = isBuyer ? RatingRole.Provider : RatingRole.Consumer;

        var result = await ratings.SubmitAsync(
            order.GuildId, RatingContext.ShopOrder, order.Id, raterId, subjectId, role, stars, comment, participantCount: 2, ct);
        switch (result)
        {
            case RatingResult.AlreadyRated:
                return ShopResult.AlreadyRated;
            case RatingResult.InvalidStars:
                return ShopResult.InvalidState;
        }

        // Both parties rated → the engine just revealed them; close the order's window so the sweep skips it.
        if (await db.CountRatingsForSourceAsync(RatingContext.ShopOrder, order.Id, ct) >= 2)
        {
            order.RatingsClosed = true;
            await db.SaveChangesAsync(ct);
        }

        await bus.PublishAsync(new ShopLifecycleNotified(
            order.GuildId, order.ListingId, order.ItemNameSnapshot, ShopLifecycleMoment.Rated, subjectId,
            $"Rated {stars}★", order.Id));

        return ShopResult.Ok;
    }

    public Task<bool> ModerateRatingAsync(Rating rating, bool moderated, CancellationToken ct = default)
        => ratings.ModerateAsync(rating, moderated, ct);

    public async Task<int> RevealClosedRatingWindowsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var due = await db.ListClosingRatingWindowsAsync(now, ct);
        var closed = 0;
        foreach (var order in due)
        {
            await ratings.RevealSourceAsync(RatingContext.ShopOrder, order.Id, ct);
            order.RatingsClosed = true;
            closed++;
        }

        if (closed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return closed;
    }

    // --- helpers ---

    /// <summary>Commission for an order: the category override if any, else the guild rate; floored, clamped.</summary>
    private async Task<long> FeeForAsync(ShopOrder order, GuildShopSettings cfg, CancellationToken ct)
    {
        var bps = cfg.CommissionBps;
        if (order.Listing?.CategoryId is { } catId
            && await db.FindCategoryInGuildAsync(order.GuildId, catId, ct) is { CommissionBpsOverride: { } ov })
        {
            bps = ov;
        }

        bps = Math.Clamp(bps, 0, 10000);
        return (long)Math.Floor(order.Amount * (double)bps / 10000.0);
    }

    private static ShopResult ToShopResult(Currencies.EscrowStatus status) => status switch
    {
        Currencies.EscrowStatus.InsufficientFunds => ShopResult.InsufficientFunds,
        Currencies.EscrowStatus.NotSpendable => ShopResult.NotSpendable,
        Currencies.EscrowStatus.CurrencyNotFound => ShopResult.NotSpendable,
        _ => ShopResult.Ok,
    };

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static IReadOnlyList<string>? CapTags(IReadOnlyList<string>? tags, int max)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        var normalized = ShopListingTag.Normalize(tags).ToList();
        if (max > 0 && normalized.Count > max)
        {
            normalized = normalized.Take(max).ToList();
        }

        return normalized;
    }

    /// <summary>Derive a URL-friendly slug: lower-case, alphanumerics kept, runs of anything else → single '-'.</summary>
    private static string Slugify(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var lastDash = false;
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (ch is '\'' or '’' or '`')
            {
                // Drop apostrophes rather than splitting on them: "Bob's" → "bobs", not "bob-s".
                continue;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "store" : slug;
    }

    /// <summary>Make <paramref name="desired"/> unique within the guild by appending -2, -3, … on collision.</summary>
    private async Task<string> UniqueSlugAsync(ulong guildId, string desired, CancellationToken ct)
    {
        if (!await db.SlugTakenAsync(guildId, desired, ct))
        {
            return desired;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{desired}-{n}";
            if (!await db.SlugTakenAsync(guildId, candidate, ct))
            {
                return candidate;
            }
        }
    }
}
