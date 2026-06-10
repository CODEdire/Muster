namespace Muster.Domain.Entities.Guilds;

/// <summary>
/// Per-guild shop / marketplace configuration — its own table (table-per-feature, like <see cref="GuildMusterSettings"/>),
/// not the <c>GuildSettings</c> JSON blob. Flat columns for the scalars; <see cref="AllowedCurrencyIds"/> is a JSON
/// column (a small, whole-read list). Also bound from configuration via <c>IOptions</c> to provide the platform
/// default values used to seed a guild's row the first time it's written.
/// </summary>
public class GuildShopSettings
{
    /// <summary>Owning guild — primary key + FK (1:1 with <c>Guild</c>). 0 on the IOptions defaults template.</summary>
    public ulong GuildId { get; set; }

    // --- Toggles ---

    /// <summary>Master switch for the player marketplace.</summary>
    public bool PlayerMarketEnabled { get; set; } = true;

    /// <summary>Allow buyers to make offers / sellers to counter (vs buy-now only).</summary>
    public bool OffersEnabled { get; set; } = true;

    /// <summary>When true, the seller marks an order delivered before the buyer-confirm clock starts.</summary>
    public bool TwoStepDelivery { get; set; }

    /// <summary>Whether buyers/sellers can rate each other and stores.</summary>
    public bool RatingsEnabled { get; set; } = true;

    /// <summary>Whether sellers may apply free-form tags to listings.</summary>
    public bool PlayerTagsEnabled { get; set; } = true;

    /// <summary>Force a category selection on every listing.</summary>
    public bool RequireCategory { get; set; }

    // --- Caps (0 = unlimited unless noted) ---

    public int MaxStoresPerSeller { get; set; } = 1;
    public int MaxActiveListingsPerSeller { get; set; } = 25;
    public int MaxOpenOffersPerBuyer { get; set; } = 10;
    public int MaxTagsPerListing { get; set; } = 5;

    /// <summary>Max concurrently-featured listings per store (0 = featuring disabled).</summary>
    public int MaxFeaturedPerStore { get; set; } = 5;

    // --- Economy ---

    /// <summary>Commission rate in basis points taken (and burned) on settlement. Clamped to the app-level
    /// ceiling. A category may override this.</summary>
    public int CommissionBps { get; set; } = 250;

    /// <summary>Flat fee to feature a listing, charged in the listing's own currency and burned (0 = free).</summary>
    public long FeaturedListingFee { get; set; }

    /// <summary>Minimum listing price (0 = off). Helps deter wash-trading at trivial prices.</summary>
    public long MinPrice { get; set; } = 1;

    /// <summary>Maximum listing price (0 = off).</summary>
    public long MaxPrice { get; set; }

    /// <summary>Currencies sellable in the market. Empty = any spendable currency.</summary>
    public List<Guid> AllowedCurrencyIds { get; set; } = [];

    // --- Timers ---

    /// <summary>Hours after delivery (or purchase, when one-step) before an unconfirmed order auto-settles.</summary>
    public int DeliveryConfirmTimeoutHours { get; set; } = 72;

    /// <summary>Two-step delivery only: hours an order may sit awaiting the seller's delivery mark before the sweep
    /// auto-cancels it and refunds the buyer (0 = never auto-cancel).</summary>
    public int UndeliveredTimeoutHours { get; set; } = 72;

    /// <summary>Hours a dispute may sit before the sweep auto-resolves it.</summary>
    public int DisputeTimeoutHours { get; set; } = 72;

    /// <summary>Hours an offer stands before it expires (and any binding hold is released).</summary>
    public int OfferExpiryHours { get; set; } = 48;

    /// <summary>Default listing lifetime in days (0 = no expiry).</summary>
    public int ListingDefaultExpiryDays { get; set; } = 30;

    /// <summary>Minimum minutes between a seller's listings (0 = no cooldown). Anti-spam.</summary>
    public int ListingCooldownMinutes { get; set; }

    /// <summary>Hours a settled order's rating window stays open before blind ratings auto-reveal.</summary>
    public int RatingWindowHours { get; set; } = 168;

    /// <summary>How long a paid feature lasts before it lapses (and its card is removed).</summary>
    public int FeaturedDurationHours { get; set; } = 72;

    // --- Channels ---

    /// <summary>Channel the shop board posts to (0 = none).</summary>
    public ulong ShopChannelId { get; set; }

    /// <summary>Channel mod/dispute alerts post to (0 = none).</summary>
    public ulong ShopModChannelId { get; set; }
}
