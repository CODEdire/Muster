using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;

namespace Muster.Persistence.Queries;

/// <summary>
/// Queries over the shop aggregates (stores, categories, listings). Write paths load a listing with its tags so
/// the consistency boundary travels together; the market board (search/sort/page + DTO projection) is built in
/// <c>ShopReadService</c>. Everything is guild-scoped.
/// </summary>
public static class ShopQueries
{
    // --- Stores ---

    public static Task<ShopStore?> FindStoreInGuildAsync(this MusterDbContext db, ulong guildId, Guid id, CancellationToken ct = default)
        => db.ShopStores.FirstOrDefaultAsync(s => s.Id == id && s.GuildId == guildId, ct);

    public static Task<ShopStore?> FindStoreBySlugAsync(this MusterDbContext db, ulong guildId, string slug, CancellationToken ct = default)
    {
        var norm = slug.Trim().ToLowerInvariant();
        return db.ShopStores.FirstOrDefaultAsync(s => s.GuildId == guildId && s.Slug == norm, ct);
    }

    public static Task<bool> SlugTakenAsync(this MusterDbContext db, ulong guildId, string slug, CancellationToken ct = default)
    {
        var norm = slug.Trim().ToLowerInvariant();
        return db.ShopStores.AnyAsync(s => s.GuildId == guildId && s.Slug == norm, ct);
    }

    public static Task<List<ShopStore>> ListStoresByOwnerAsync(this MusterDbContext db, ulong guildId, ulong ownerId, CancellationToken ct = default)
        => db.ShopStores.Where(s => s.GuildId == guildId && s.OwnerId == ownerId)
            .OrderBy(s => s.Name).ToListAsync(ct);

    public static Task<int> CountStoresByOwnerAsync(this MusterDbContext db, ulong guildId, ulong ownerId, CancellationToken ct = default)
        => db.ShopStores.CountAsync(s => s.GuildId == guildId && s.OwnerId == ownerId && !s.Closed, ct);

    /// <summary>Open (non-closed) stores in a guild — for re-syncing their home cards to the shop channel.</summary>
    public static Task<List<ShopStore>> ListActiveStoresAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.ShopStores.Where(s => s.GuildId == guildId && !s.Closed).ToListAsync(ct);

    /// <summary>Count of a store's currently-active listings (for its home card).</summary>
    public static Task<int> CountActiveListingsInStoreAsync(this MusterDbContext db, Guid storeId, CancellationToken ct = default)
        => db.ShopListings.CountAsync(l => l.StoreId == storeId && l.Status == ShopListingStatus.Active, ct);

    // --- Categories ---

    public static Task<List<ShopCategory>> ListCategoriesAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.ShopCategories.Where(c => c.GuildId == guildId).OrderBy(c => c.Sort).ThenBy(c => c.Name).ToListAsync(ct);

    public static Task<ShopCategory?> FindCategoryInGuildAsync(this MusterDbContext db, ulong guildId, Guid id, CancellationToken ct = default)
        => db.ShopCategories.FirstOrDefaultAsync(c => c.Id == id && c.GuildId == guildId, ct);

    // --- Store types (admin taxonomy, like categories but for stores) ---

    public static Task<List<ShopStoreType>> ListStoreTypesAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.ShopStoreTypes.Where(t => t.GuildId == guildId).OrderBy(t => t.Sort).ThenBy(t => t.Name).ToListAsync(ct);

    public static Task<ShopStoreType?> FindStoreTypeInGuildAsync(this MusterDbContext db, ulong guildId, Guid id, CancellationToken ct = default)
        => db.ShopStoreTypes.FirstOrDefaultAsync(t => t.Id == id && t.GuildId == guildId, ct);

    // --- Listings ---

    /// <summary>Load a listing with its tags + owning store — the full aggregate for a write path.</summary>
    public static Task<ShopListing?> FindListingInGuildAsync(this MusterDbContext db, ulong guildId, Guid id, CancellationToken ct = default)
        => db.ShopListings.Include(l => l.Tags).Include(l => l.Store)
            .FirstOrDefaultAsync(l => l.Id == id && l.GuildId == guildId, ct);

    /// <summary>Active listings in one store (storefront page).</summary>
    public static Task<List<ShopListing>> ListByStoreAsync(this MusterDbContext db, Guid storeId, CancellationToken ct = default)
        => db.ShopListings.Include(l => l.Tags)
            .Where(l => l.StoreId == storeId && l.Status == ShopListingStatus.Active)
            .OrderByDescending(l => l.CreatedAt).ToListAsync(ct);

    /// <summary>How many active listings a seller currently has across the guild (per-seller cap check).</summary>
    public static Task<int> CountActiveListingsBySellerAsync(this MusterDbContext db, ulong guildId, ulong sellerId, CancellationToken ct = default)
        => db.ShopListings.CountAsync(l => l.GuildId == guildId && l.SellerId == sellerId && l.Status == ShopListingStatus.Active, ct);

    /// <summary>A seller's active listings (for the feature picker / autocomplete).</summary>
    public static Task<List<ShopListing>> ListActiveListingsBySellerAsync(this MusterDbContext db, ulong guildId, ulong sellerId, CancellationToken ct = default)
        => db.ShopListings.Where(l => l.GuildId == guildId && l.SellerId == sellerId && l.Status == ShopListingStatus.Active)
            .OrderByDescending(l => l.CreatedAt).ToListAsync(ct);

    /// <summary>How many of a store's listings are currently featured (active feature window) — per-store cap check.
    /// Only Active listings count: a sold-out listing keeps its window (to carry on relist) but is hidden everywhere,
    /// so it must not consume a featured slot.</summary>
    public static Task<int> CountFeaturedInStoreAsync(this MusterDbContext db, Guid storeId, DateTimeOffset now, CancellationToken ct = default)
        => db.ShopListings.CountAsync(l => l.StoreId == storeId && l.Status == ShopListingStatus.Active
            && l.FeaturedUntil != null && l.FeaturedUntil > now, ct);

    /// <summary>Featured listings whose window has lapsed (or that are no longer active) — for the unfeature sweep.</summary>
    public static Task<List<ShopListing>> ListExpiredFeaturedAsync(this MusterDbContext db, DateTimeOffset now, CancellationToken ct = default)
        => db.ShopListings.Where(l => l.FeaturedUntil != null && l.FeaturedUntil <= now).ToListAsync(ct);

    /// <summary>Active listings past their <c>ExpiresAt</c> — the auto-delist sweep transitions them to Expired.</summary>
    public static Task<List<ShopListing>> ListExpiredListingsAsync(this MusterDbContext db, DateTimeOffset now, CancellationToken ct = default)
        => db.ShopListings.Where(l => l.Status == ShopListingStatus.Active && l.ExpiresAt != null && l.ExpiresAt <= now).ToListAsync(ct);

    /// <summary>Currently-featured, active listings in a guild — for re-syncing the shop channel's cards.</summary>
    public static Task<List<ShopListing>> ListActiveFeaturedAsync(this MusterDbContext db, ulong guildId, DateTimeOffset now, CancellationToken ct = default)
        => db.ShopListings.Where(l => l.GuildId == guildId && l.Status == ShopListingStatus.Active
            && l.FeaturedUntil != null && l.FeaturedUntil > now).ToListAsync(ct);

    /// <summary>The seller's most recent listing time, for the anti-spam cooldown (null = none yet).</summary>
    public static async Task<DateTimeOffset?> LatestListingAtBySellerAsync(this MusterDbContext db, ulong guildId, ulong sellerId, CancellationToken ct = default)
        => await db.ShopListings.Where(l => l.GuildId == guildId && l.SellerId == sellerId)
            .OrderByDescending(l => l.CreatedAt).Select(l => (DateTimeOffset?)l.CreatedAt).FirstOrDefaultAsync(ct);

    // --- Image blobs (orphan sweep) ---

    /// <summary>Every blob key any live row could still reference: a listing's image + thumbnail (kept for ALL
    /// statuses — sold-out/expired listings stay as relistable history), a store's banner + logo, and a disputed
    /// order's evidence. The orphan sweep deletes container blobs outside this set. Returns an ordinal hash set
    /// (blob keys are case-sensitive Guid-derived names).</summary>
    public static async Task<HashSet<string>> ListReferencedImageKeysAsync(this MusterDbContext db, CancellationToken ct = default)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        var listingKeys = await db.ShopListings
            .Where(l => l.ImageKey != null || l.ThumbKey != null)
            .Select(l => new { l.ImageKey, l.ThumbKey }).ToListAsync(ct);
        foreach (var k in listingKeys)
        {
            if (k.ImageKey != null) { set.Add(k.ImageKey); }
            if (k.ThumbKey != null) { set.Add(k.ThumbKey); }
        }

        var storeKeys = await db.ShopStores
            .Where(s => s.BannerImageKey != null || s.LogoImageKey != null)
            .Select(s => new { s.BannerImageKey, s.LogoImageKey }).ToListAsync(ct);
        foreach (var k in storeKeys)
        {
            if (k.BannerImageKey != null) { set.Add(k.BannerImageKey); }
            if (k.LogoImageKey != null) { set.Add(k.LogoImageKey); }
        }

        var evidenceKeys = await db.ShopOrders
            .Where(o => o.DisputeEvidenceKey != null)
            .Select(o => o.DisputeEvidenceKey!).ToListAsync(ct);
        foreach (var k in evidenceKeys)
        {
            set.Add(k);
        }

        return set;
    }

    // --- Orders ---

    /// <summary>Load an order (with its listing) scoped to a guild — the write-path aggregate.</summary>
    public static Task<ShopOrder?> FindOrderInGuildAsync(this MusterDbContext db, ulong guildId, Guid id, CancellationToken ct = default)
        => db.ShopOrders.Include(o => o.Listing).FirstOrDefaultAsync(o => o.Id == id && o.GuildId == guildId, ct);

    /// <summary>Orders awaiting buyer confirmation (PendingDelivery / Delivered) across all guilds, for the
    /// auto-settle sweep. The caller applies each guild's confirm timeout.</summary>
    public static Task<List<ShopOrder>> ListAwaitingConfirmationAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.ShopOrders.Include(o => o.Listing)
            .Where(o => o.Status == ShopOrderStatus.PendingDelivery || o.Status == ShopOrderStatus.Delivered)
            .ToListAsync(ct);

    /// <summary>A buyer's orders (most recent first) for the my-orders surface.</summary>
    public static Task<List<ShopOrder>> ListOrdersByBuyerAsync(this MusterDbContext db, ulong guildId, ulong buyerId, CancellationToken ct = default)
        => db.ShopOrders.AsNoTracking().Where(o => o.GuildId == guildId && o.BuyerId == buyerId)
            .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);

    /// <summary>A seller's incoming orders (most recent first).</summary>
    public static Task<List<ShopOrder>> ListOrdersBySellerAsync(this MusterDbContext db, ulong guildId, ulong sellerId, CancellationToken ct = default)
        => db.ShopOrders.AsNoTracking().Where(o => o.GuildId == guildId && o.SellerId == sellerId)
            .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);

    /// <summary>Disputed orders in a guild (oldest first) — the shop manager's arbitration queue.</summary>
    public static Task<List<ShopOrder>> ListDisputedInGuildAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.ShopOrders.AsNoTracking().Where(o => o.GuildId == guildId && o.Status == ShopOrderStatus.Disputed)
            .OrderBy(o => o.StatusChangedAt).ToListAsync(ct);

    /// <summary>Disputed orders across all guilds (with listing) for the auto-resolve sweep.</summary>
    public static Task<List<ShopOrder>> ListDisputedAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.ShopOrders.Include(o => o.Listing).Where(o => o.Status == ShopOrderStatus.Disputed).ToListAsync(ct);

    /// <summary>Count a listing's live orders/offers (non-terminal) — used to lock editing once trade has started.</summary>
    public static Task<int> CountActiveOrdersForListingAsync(this MusterDbContext db, Guid listingId, CancellationToken ct = default)
        => db.ShopOrders.CountAsync(o => o.ListingId == listingId
            && (o.Status == ShopOrderStatus.PendingDelivery || o.Status == ShopOrderStatus.Delivered
                || o.Status == ShopOrderStatus.Disputed || o.Status == ShopOrderStatus.OfferPending), ct);

    // --- Offers (orders in OfferPending) ---

    /// <summary>How many open (pending) offers a buyer has — for the per-buyer offer cap.</summary>
    public static Task<int> CountOpenOffersByBuyerAsync(this MusterDbContext db, ulong guildId, ulong buyerId, CancellationToken ct = default)
        => db.ShopOrders.CountAsync(o => o.GuildId == guildId && o.BuyerId == buyerId && o.Status == ShopOrderStatus.OfferPending, ct);

    /// <summary>Other open offers on a listing (excluding <paramref name="exceptOrderId"/>) — competing offers to
    /// decline when one is accepted on a now-sold-out listing.</summary>
    public static Task<List<ShopOrder>> ListOpenOffersForListingAsync(this MusterDbContext db, Guid listingId, Guid exceptOrderId, CancellationToken ct = default)
        => db.ShopOrders.Where(o => o.ListingId == listingId && o.Id != exceptOrderId && o.Status == ShopOrderStatus.OfferPending).ToListAsync(ct);

    /// <summary>All open offers across guilds (with listing) for the offer-expiry sweep.</summary>
    public static Task<List<ShopOrder>> ListOpenOffersAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.ShopOrders.Include(o => o.Listing).Where(o => o.Status == ShopOrderStatus.OfferPending).ToListAsync(ct);

    // --- Ratings ---

    /// <summary>Settled orders whose blind-rating window has lapsed but isn't yet closed — for the reveal sweep.</summary>
    public static Task<List<ShopOrder>> ListClosingRatingWindowsAsync(this MusterDbContext db, DateTimeOffset now, CancellationToken ct = default)
        => db.ShopOrders
            .Where(o => o.Status == ShopOrderStatus.Settled && !o.RatingsClosed
                && o.RatingWindowClosesAt != null && o.RatingWindowClosesAt <= now)
            .ToListAsync(ct);

    // --- Wallet escrow (available-vs-held) ---

    /// <summary>Currency a member currently has locked in shop escrow for one currency — the sum of their open orders
    /// (paid into escrow, not yet settled or refunded). Drives the wallet's available-vs-held split
    /// (<c>available = balance − held</c>). Held states: pending delivery, delivered (awaiting confirm), disputed,
    /// and binding offers awaiting the seller.</summary>
    public static async Task<long> MemberHeldFundsAsync(
        this MusterDbContext db, ulong guildId, ulong userId, Guid currencyId, CancellationToken ct = default)
        => await db.ShopOrders
            .Where(o => o.GuildId == guildId && o.BuyerId == userId && o.CurrencyId == currencyId
                && (o.Status == ShopOrderStatus.PendingDelivery || o.Status == ShopOrderStatus.Delivered
                    || o.Status == ShopOrderStatus.Disputed || o.Status == ShopOrderStatus.OfferPending))
            .SumAsync(o => (long?)o.Amount, ct) ?? 0;

    /// <summary>Count of a member's open (held) shop orders for one currency — the "N open orders" hint next to held
    /// funds in the wallet.</summary>
    public static Task<int> MemberHeldOrderCountAsync(
        this MusterDbContext db, ulong guildId, ulong userId, Guid currencyId, CancellationToken ct = default)
        => db.ShopOrders
            .CountAsync(o => o.GuildId == guildId && o.BuyerId == userId && o.CurrencyId == currencyId
                && (o.Status == ShopOrderStatus.PendingDelivery || o.Status == ShopOrderStatus.Delivered
                    || o.Status == ShopOrderStatus.Disputed || o.Status == ShopOrderStatus.OfferPending), ct);
}
