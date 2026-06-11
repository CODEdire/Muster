namespace Muster.Contracts;

// Shop / player-marketplace message contracts: the lifecycle notification + the CQRS command set invoked via
// IMessageBus by every surface (bot/web/api). The handler is the single funnel where authorization lives.
// Phase 1 covers stores, categories, and listing CRUD; ordering/offers/ratings commands land in later phases.

/// <summary>
/// Emitted by the shop service whenever a listing / order transitions. Consumers (a Discord notifier, an
/// external connector, …) subscribe and fan out per <see cref="Moment"/>. <paramref name="TargetUserId"/> is who
/// should hear about it (null = shop board / managers).
/// </summary>
public record ShopLifecycleNotified(
    ulong GuildId,
    Guid ListingId,
    string ListingName,
    ShopLifecycleMoment Moment,
    ulong? TargetUserId,
    string Detail,
    Guid? OrderId = null);

// --- Stores ---

/// <summary>Open a new storefront. A member store needs ShopCreator; a <see cref="ShopStoreOrigin.Guild"/> store
/// needs ShopManager and burns the buyer's coins on each sale. Slug is derived from the name when omitted.</summary>
public record CreateStore(ulong GuildId, ulong ActorId, string Name, string? Description = null, string? Slug = null, Guid? StoreTypeId = null, ShopStoreOrigin Origin = ShopStoreOrigin.Member) : IGuildCommand;

/// <summary>Delete a store and its listings (owner + ShopCreator, or a shop manager). Refused while the store
/// has active orders.</summary>
public record DeleteStore(ulong GuildId, ulong ActorId, Guid StoreId) : IGuildCommand;

/// <summary>Edit a store's presentation (owner + ShopCreator, or a shop manager).</summary>
public record EditStore(
    ulong GuildId,
    ulong ActorId,
    Guid StoreId,
    string? Name = null,
    string? Description = null,
    string? BannerImageKey = null,
    string? LogoImageKey = null,
    string? AccentColor = null,
    bool? Closed = null,
    Guid? StoreTypeId = null,
    ShopStoreOrigin? Origin = null) : IGuildCommand;  // null = leave unchanged; changing it requires ShopManager

// --- Categories (shop manager) ---

/// <summary>Create a guild-curated listing category (ShopManager / Admin).</summary>
public record CreateCategory(ulong GuildId, ulong ActorId, string Name, int Sort = 0, int? CommissionBpsOverride = null, string? Icon = null) : IGuildCommand;

/// <summary>Edit a listing category (ShopManager / Admin). <paramref name="CommissionBpsOverride"/> is applied
/// as given (null clears the override → category uses the guild default).</summary>
public record EditCategory(ulong GuildId, ulong ActorId, Guid CategoryId, string Name, int Sort, int? CommissionBpsOverride, string? Icon = null) : IGuildCommand;

/// <summary>Delete a listing category (ShopManager / Admin). Listings in it fall back to no category.</summary>
public record DeleteCategory(ulong GuildId, ulong ActorId, Guid CategoryId) : IGuildCommand;

// --- Store types (admin taxonomy) ---

/// <summary>Create a guild store-type (ShopManager / Admin).</summary>
public record CreateStoreType(ulong GuildId, ulong ActorId, string Name, int Sort = 0, string? Icon = null) : IGuildCommand;

/// <summary>Rename / reorder / re-icon a store-type (ShopManager / Admin).</summary>
public record EditStoreType(ulong GuildId, ulong ActorId, Guid StoreTypeId, string Name, int Sort, string? Icon = null) : IGuildCommand;

/// <summary>Delete a store-type (ShopManager / Admin). Stores of that type fall back to no type.</summary>
public record DeleteStoreType(ulong GuildId, ulong ActorId, Guid StoreTypeId) : IGuildCommand;

/// <summary>Apply the default shop categories + store types to this guild (Admin). <paramref name="Restore"/> re-adds
/// every missing default (incl. ones deleted earlier); otherwise only newly-introduced defaults are added.</summary>
public record SeedGuildDefaults(ulong GuildId, ulong ActorId, bool Restore = false) : IGuildCommand;

// --- Listings ---

/// <summary>Post a listing in one of the actor's stores (ShopCreator + store owner). Image keys reference
/// already-uploaded blobs.</summary>
public record PostListing(
    ulong GuildId,
    ulong ActorId,
    Guid StoreId,
    string Name,
    string Currency,
    long Price,
    string? Description = null,
    Guid? CategoryId = null,
    int Quantity = 1,
    string? ImageKey = null,
    string? ThumbKey = null,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<string>? Tags = null,
    bool AcceptsOffers = true) : IGuildCommand;

/// <summary>Patch a listing before it has any orders/offers (owner + ShopCreator, or a shop manager).</summary>
public record EditListing(
    ulong GuildId,
    ulong ActorId,
    Guid ListingId,
    string? Name = null,
    string? Description = null,
    long? Price = null,
    Guid? CategoryId = null,
    int? Quantity = null,
    string? ImageKey = null,
    string? ThumbKey = null,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<string>? Tags = null,
    bool? AcceptsOffers = null) : IGuildCommand;

/// <summary>Withdraw a listing (owner + ShopCreator). A shop manager uses this as a moderation takedown;
/// <paramref name="Reason"/> is recorded + shown to the seller.</summary>
public record CancelListing(ulong GuildId, ulong ActorId, Guid ListingId, string? Reason = null) : IGuildCommand;

/// <summary>Feature a listing (owner + ShopCreator): burns the guild's featured fee in the listing's currency and
/// posts a promoted card to the shop channel for the configured duration. Capped per store.</summary>
public record FeatureListing(ulong GuildId, ulong ActorId, Guid ListingId) : IGuildCommand;

/// <summary>Un-feature a listing early (owner + ShopCreator) so a different one can take the slot. The fee is NOT
/// refunded.</summary>
public record UnfeatureListing(ulong GuildId, ulong ActorId, Guid ListingId) : IGuildCommand;

/// <summary>Re-post / refresh the whole shop channel (every store's home card + every featured listing card)
/// (owner + ShopCreator). Used after the shop channel is first linked or changed, or as a manual repair.</summary>
public record ResyncShopChannel(ulong GuildId, ulong ActorId) : IGuildCommand;

/// <summary>Re-post / refresh one store's home card (+ its featured cards) in the shop channel — the per-store
/// repair a shop owner triggers from the store page.</summary>
public record ResyncShopStore(ulong GuildId, ulong ActorId, Guid StoreId) : IGuildCommand;

/// <summary>Emitted when a store changes (created/edited/deleted) so the bot can (re)render its home card — the
/// persistent per-store presence in the shop channel.</summary>
public record ShopStoreChanged(ulong GuildId, Guid StoreId);

// --- Orders (buy-now escrow) ---

/// <summary>Buy a listing now: holds the buyer's funds in escrow and opens an order awaiting receipt confirmation.</summary>
public record PurchaseListing(ulong GuildId, ulong ActorId, Guid ListingId, int Quantity = 1) : IGuildCommand;

/// <summary>Seller marks a pending order delivered (two-step delivery) — starts the buyer's confirm clock.</summary>
public record MarkDelivered(ulong GuildId, ulong ActorId, Guid OrderId) : IGuildCommand;

/// <summary>Buyer confirms they received the item: escrow settles to the seller (less the burned commission).</summary>
public record ConfirmReceipt(ulong GuildId, ulong ActorId, Guid OrderId) : IGuildCommand;

/// <summary>Seller (or a shop manager) cancels a pending order: escrow is refunded to the buyer, stock released.</summary>
public record SellerCancelOrder(ulong GuildId, ulong ActorId, Guid OrderId) : IGuildCommand;

/// <summary>Buyer or seller raises a dispute on a pending order, freezing escrow for a shop manager to arbitrate.</summary>
public record DisputeOrder(ulong GuildId, ulong ActorId, Guid OrderId, string Reason, string? EvidenceKey = null) : IGuildCommand;

/// <summary>A shop manager resolves a disputed order: pay the seller (settle + burn) or refund the buyer.</summary>
public record ArbitrateOrder(ulong GuildId, ulong ActorId, Guid OrderId, bool PaySeller) : IGuildCommand;

// --- Offers (binding hold, seller approves) ---

/// <summary>Buyer makes a binding price offer on a listing — funds are held until the seller responds.</summary>
public record MakeOffer(ulong GuildId, ulong ActorId, Guid ListingId, long Amount, int Quantity = 1) : IGuildCommand;

/// <summary>The awaited party accepts the current offer price — it becomes a live order.</summary>
public record AcceptOffer(ulong GuildId, ulong ActorId, Guid OrderId) : IGuildCommand;

/// <summary>The awaited party counters with a new price — bounces the negotiation back to the other side.</summary>
public record CounterOffer(ulong GuildId, ulong ActorId, Guid OrderId, long Amount) : IGuildCommand;

/// <summary>Decline / end a pending offer (buyer, seller, or shop manager) — held funds refunded to the buyer.</summary>
public record DeclineOffer(ulong GuildId, ulong ActorId, Guid OrderId) : IGuildCommand;

/// <summary>Buyer withdraws their own pending offer — held funds refunded.</summary>
public record WithdrawOffer(ulong GuildId, ulong ActorId, Guid OrderId) : IGuildCommand;

// --- Ratings (blind-mutual) ---

/// <summary>Leave a 1–5★ rating on a settled order (buyer→seller or seller→buyer). Hidden until both rate or the
/// window closes.</summary>
public record RateOrder(ulong GuildId, ulong ActorId, Guid OrderId, int Stars, string? Comment = null) : IGuildCommand;

/// <summary>A shop manager hides/unhides a rating (abuse takedown) — keeps the record, drops it from view + stats.</summary>
public record ModerateRating(ulong GuildId, ulong ActorId, Guid RatingId, bool Hidden) : IGuildCommand;
