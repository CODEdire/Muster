using Muster.Contracts;
using Muster.Domain.Entities.Ratings;
using Muster.Domain.Entities.Shops;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>Outcome of a shop operation. Ok plus the reasons one can be refused. Adapters map the name to
/// user-facing text (<c>ShopResultPresentation</c>) / an HTTP status.</summary>
public enum ShopResult
{
    Ok,
    NotFound,
    NotActive,
    OutOfStock,
    AlreadyPurchased,
    OfferCapReached,
    StoreCapReached,
    ListingCapReached,
    Cooldown,
    SlugTaken,
    BelowFloor,
    AboveCeiling,
    InsufficientFunds,
    NotSpendable,
    NotSeller,
    Forbidden,
    InvalidState,
    AlreadyRated,
    RatingWindowClosed,
    /// <summary>The listing can't be edited because it has live orders/offers.</summary>
    HasOrders,
    /// <summary>The store already has the maximum number of featured listings.</summary>
    FeaturedCapReached,
    /// <summary>Two-step delivery: the seller hasn't marked the order delivered yet, so it can't be confirmed.</summary>
    NotDelivered,
    /// <summary>The guild requires a category and none was selected.</summary>
    CategoryRequired,
    /// <summary>The listing needs a name.</summary>
    NameRequired,
    /// <summary>The price must be greater than zero.</summary>
    InvalidPrice,
}

/// <summary>
/// The shop / player-marketplace service. Phase 1 covers stores, categories, and listing CRUD; ordering,
/// offers, disputes, and ratings land in later phases. Authorization is enforced by the command handlers
/// (via <see cref="IShopAuthorizer"/>) before these are called — the service owns validation + domain mutation
/// and publishes <c>ShopLifecycleNotified</c> on listing transitions.
/// </summary>
public interface IShopService
{
    Task<(ShopResult Result, Guid? StoreId)> CreateStoreAsync(
        ulong guildId, ulong ownerId, string name, string? description, string? slug, Guid? storeTypeId = null,
        ShopStoreOrigin origin = ShopStoreOrigin.Member, CancellationToken ct = default);

    Task<ShopResult> EditStoreAsync(
        ShopStore store, string? name, string? description, string? bannerImageKey, string? logoImageKey,
        string? accentColor, bool? closed, Guid? storeTypeId, ShopStoreOrigin? origin = null, CancellationToken ct = default);

    Task<ShopResult> DeleteStoreAsync(ShopStore store, CancellationToken ct = default);

    Task<(ShopResult Result, Guid? CategoryId)> CreateCategoryAsync(
        ulong guildId, string name, int sort, int? commissionBpsOverride, string? icon = null, CancellationToken ct = default);

    Task<ShopResult> EditCategoryAsync(ShopCategory category, string name, int sort, int? commissionBpsOverride, string? icon = null, CancellationToken ct = default);

    Task<ShopResult> DeleteCategoryAsync(ShopCategory category, CancellationToken ct = default);

    Task<(ShopResult Result, Guid? StoreTypeId)> CreateStoreTypeAsync(ulong guildId, string name, int sort, string? icon = null, CancellationToken ct = default);

    Task<ShopResult> EditStoreTypeAsync(ShopStoreType type, string name, int sort, string? icon = null, CancellationToken ct = default);

    Task<ShopResult> DeleteStoreTypeAsync(ShopStoreType type, CancellationToken ct = default);

    Task<(ShopResult Result, Guid? ListingId)> PostListingAsync(
        ShopStore store, ulong sellerId, string name, string currencyCode, long price, string? description,
        Guid? categoryId, int quantity, bool acceptsOffers, string? imageKey, string? thumbKey, DateTimeOffset? expiresAt,
        IReadOnlyList<string>? tags, CancellationToken ct = default);

    Task<ShopResult> EditListingAsync(
        ShopListing listing, string? name, string? description, long? price, Guid? categoryId, int? quantity,
        bool? acceptsOffers, string? imageKey, string? thumbKey, DateTimeOffset? expiresAt, IReadOnlyList<string>? tags, CancellationToken ct = default);

    /// <summary>Relist a sold-out/expired listing as a fresh copy (new id, fresh stock + <c>CreatedAt</c>, fresh board
    /// card): the seller's reputation carries (ratings are per-seller), the image blob is copied so the old listing
    /// keeps its thumbnail, and any remaining featured window moves over without re-charging. The old listing stays
    /// as history, linked via <c>RelistedFromId</c>. <paramref name="priceOverride"/> null keeps the old price.</summary>
    Task<(ShopResult Result, Guid? ListingId)> RelistAsync(
        ShopListing old, int quantity, long? priceOverride, CancellationToken ct = default);

    /// <summary>Add stock to an Active listing (top-up). Increment-only and deliberately bypasses the edit-lock —
    /// adding units never changes terms under an in-flight buyer. Sold-out listings restock via <see cref="RelistAsync"/>.</summary>
    Task<ShopResult> AdjustStockAsync(ShopListing listing, int addUnits, CancellationToken ct = default);

    /// <summary>Delist a listing. <paramref name="actorId"/> records who (seller = withdrawal, anyone else =
    /// moderator takedown); <paramref name="reason"/> is shown to the seller.</summary>
    Task<ShopResult> CancelListingAsync(ShopListing listing, ulong actorId, string? reason, CancellationToken ct = default);

    /// <summary>Feature a listing: burn the guild's featured fee (in the listing's currency) and promote it for the
    /// configured duration. Refused if the store is at its featured cap, the listing isn't active, or funds are short.</summary>
    Task<ShopResult> FeatureListingAsync(ShopListing listing, CancellationToken ct = default);

    /// <summary>Un-feature a listing early (no fee refund) so its featured slot frees up.</summary>
    Task<ShopResult> UnfeatureListingAsync(ShopListing listing, CancellationToken ct = default);

    /// <summary>Clear listings whose feature window has lapsed (and publish so the channel card is removed).
    /// Returns the count unfeatured.</summary>
    Task<int> ExpireFeaturedDueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Auto-delist active listings past their <c>ExpiresAt</c> (transition to Expired). Returns the count.</summary>
    Task<int> ExpireListingsDueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Delete blobs in the <c>shopimages</c> container that no live row references (relisting copies a
    /// listing's image to a fresh key, so blobs accumulate). Blobs uploaded within the grace window before
    /// <paramref name="now"/> are skipped so the sweep can't race an in-flight upload whose DB row hasn't committed.
    /// Returns the count deleted. Run on a slow cadence — it enumerates the whole container.</summary>
    Task<int> SweepOrphanImagesAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Re-post the whole shop channel — every open store's home card + every featured listing's card
    /// (after the channel is linked/changed, or as a manual repair). Returns the number of stores re-synced.</summary>
    Task<int> ResyncShopChannelAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>Re-post one store's home card (+ its featured cards) — the per-store repair a shop owner triggers.</summary>
    Task<ShopResult> ResyncStoreAsync(ulong guildId, Guid storeId, CancellationToken ct = default);

    // --- Orders (buy-now escrow) ---

    Task<(ShopResult Result, Guid? OrderId)> PurchaseAsync(ShopListing listing, ulong buyerId, int quantity, CancellationToken ct = default);

    /// <summary>Seller marks a pending order delivered (two-step delivery) — restarts the buyer-confirm clock.</summary>
    Task<ShopResult> MarkDeliveredAsync(ShopOrder order, CancellationToken ct = default);

    Task<ShopResult> ConfirmReceiptAsync(ShopOrder order, CancellationToken ct = default);

    Task<ShopResult> SellerCancelOrderAsync(ShopOrder order, CancellationToken ct = default);

    /// <summary>Settle orders whose buyer-confirm window has lapsed (auto-settle to the seller). Returns the count.</summary>
    Task<int> AutoSettleDueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Cancel + refund two-step orders the seller never marked delivered within the window. Returns the count.</summary>
    Task<int> AutoCancelUndeliveredDueAsync(DateTimeOffset now, CancellationToken ct = default);

    Task<ShopResult> DisputeOrderAsync(ShopOrder order, ulong disputedBy, string reason, string? evidenceKey, CancellationToken ct = default);

    /// <summary>Resolve a disputed order: pay the seller (settle + burn) or refund the buyer.</summary>
    Task<ShopResult> ArbitrateOrderAsync(ShopOrder order, ulong resolverId, bool paySeller, CancellationToken ct = default);

    /// <summary>Auto-resolve disputes left past the guild's timeout — in favour of the non-disputing party
    /// (raising a dispute and ghosting it shouldn't win). Returns the count resolved.</summary>
    Task<int> AutoResolveDisputesAsync(DateTimeOffset now, CancellationToken ct = default);

    // --- Offers (binding hold, seller approves) ---

    /// <summary>Make a binding price offer on a listing: holds the buyer's requested <paramref name="amount"/> in
    /// escrow and opens an offer order awaiting the seller's accept/decline.</summary>
    Task<(ShopResult Result, Guid? OrderId)> MakeOfferAsync(ShopListing listing, ulong buyerId, long amount, int quantity, CancellationToken ct = default);

    /// <summary>The awaited party accepts the current offer price → it becomes a live order (held funds carry
    /// over; a buyer accepting a seller counter holds now). Stock reserved; competing offers declined + refunded.</summary>
    Task<ShopResult> AcceptOfferAsync(ShopOrder order, ulong actorId, CancellationToken ct = default);

    /// <summary>The awaited party counters with a new price — bounces the negotiation back (releases/re-holds funds
    /// so a buyer-proposed price is always the held one).</summary>
    Task<ShopResult> CounterOfferAsync(ShopOrder order, ulong actorId, long amount, CancellationToken ct = default);

    /// <summary>Decline / withdraw a pending offer (any party) — refunds held funds.</summary>
    Task<ShopResult> DeclineOfferAsync(ShopOrder order, ulong actorId, CancellationToken ct = default);

    /// <summary>Expire offers left unanswered past the guild's offer window (refund the buyer). Returns the count.</summary>
    Task<int> AutoExpireOffersAsync(DateTimeOffset now, CancellationToken ct = default);

    // --- Ratings (blind-mutual) ---

    /// <summary>Leave a 1–5★ rating on a settled order (buyer→seller or seller→buyer). Blind until both parties
    /// rate or the window closes. Refused off a settled order, after the window, or on a repeat.</summary>
    Task<ShopResult> SubmitRatingAsync(ShopOrder order, ulong raterId, int stars, string? comment, CancellationToken ct = default);

    /// <summary>Hide/unhide a rating from visibility + aggregates (shop-manager abuse takedown).</summary>
    Task<bool> ModerateRatingAsync(Rating rating, bool moderated, CancellationToken ct = default);

    /// <summary>Reveal ratings on settled orders whose blind window has lapsed. Returns the count of orders closed.</summary>
    Task<int> RevealClosedRatingWindowsAsync(DateTimeOffset now, CancellationToken ct = default);
}
