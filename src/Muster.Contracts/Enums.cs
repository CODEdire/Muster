namespace Muster.Contracts;

// Shared, dependency-free enums that travel on message contracts (and are reused by the domain). They live
// here so Muster.Contracts references nothing — everything else can depend on it.

/// <summary>Who created a quest and how its reward is funded.</summary>
public enum QuestOrigin
{
    /// <summary>Created by the guild; reward is minted (new currency issued).</summary>
    Guild = 0,
    /// <summary>A player bounty; reward is escrowed from the poster's own balance and transferred on completion.</summary>
    Player = 1,
}

/// <summary>How a tracking session's spendable-coin mint is gated by its linked muster(s) at close. Points are
/// never gated. A round muster is just another linked muster; combine them via this mode.</summary>
public enum SessionCoinGate
{
    /// <summary>No gating — every eligible attendee earns the session coin (the default, and the behavior when no
    /// muster is linked).</summary>
    None = 0,

    /// <summary>Coin minted to an attendee who checked into ANY linked muster (union).</summary>
    Any = 1,

    /// <summary>Coin minted only to an attendee who checked into ALL linked musters (intersection).</summary>
    All = 2,
}

/// <summary>What happens when a standalone muster's active window ends (auto-close / expiry).</summary>
public enum MusterResolveMode
{
    /// <summary>Auto-resolve: the muster closes and pays its roster immediately (the default).</summary>
    Pay = 0,

    /// <summary>Review: the muster soft-closes into a pending state (no check-ins, not paid) so an owner/manager can
    /// vet the roster, then Approve &amp; pay (close) or Discard (cancel). Manual "Close &amp; pay" still finalizes
    /// immediately regardless of this mode.</summary>
    Review = 1,
}

/// <summary>Where an auto-created (on session open) muster posts its card.</summary>
public enum MusterAutoCreateChannel
{
    /// <summary>The guild's configured default muster channel (or, if none, no card is posted).</summary>
    DefaultChannel = 0,

    /// <summary>The session's own (voice) channel — but only when the allow-list permits it; otherwise it falls
    /// back to the default channel.</summary>
    SessionChannel = 1,
}

/// <summary>Difficulty tier for a guild quest, driving the bonus POINTS reward via guild config. S is hardest.</summary>
public enum QuestTier
{
    None = 0,
    E = 1,
    D = 2,
    C = 3,
    B = 4,
    A = 5,
    S = 6,
}

/// <summary>A point in a quest's lifecycle worth telling someone about (Discord DM/post, external API, etc.).</summary>
public enum QuestLifecycleMoment
{
    Created,
    PendingApproval,
    Accepted,
    RejectedAtIntake,
    Claimed,
    Submitted,
    RevisionRequested,
    AwaitingFinalApproval,
    Settled,
    Refunded,
    Disputed,
    Expired,
    Released,
    Reopened,
    Rejected, // appended (not reordered) — the SQL queue serialises this enum by ordinal
}

// --- Shop / player marketplace ---

/// <summary>Lifecycle of a shop listing (a sell offer for an in-game item).</summary>
public enum ShopListingStatus
{
    /// <summary>Saved but not yet published to the board.</summary>
    Draft = 0,
    /// <summary>Live and purchasable.</summary>
    Active = 1,
    /// <summary>Out of stock — every unit sold (stackable listings).</summary>
    SoldOut = 2,
    /// <summary>Withdrawn by the seller (or a moderator takedown).</summary>
    Cancelled = 3,
    /// <summary>Passed its expiry without selling out.</summary>
    Expired = 4,
}

/// <summary>Lifecycle of a shop order — the buyer's escrowed purchase, settled on confirmation.</summary>
public enum ShopOrderStatus
{
    /// <summary>Paid into escrow; awaiting the buyer's receipt confirmation.</summary>
    PendingDelivery = 0,
    /// <summary>Seller marked the item handed over (two-step delivery); buyer-confirm clock running.</summary>
    Delivered = 1,
    /// <summary>Buyer confirmed (or auto-settled): escrow paid out to the seller, commission burned.</summary>
    Settled = 2,
    /// <summary>Either party raised a dispute; awaiting a shop manager's arbitration.</summary>
    Disputed = 3,
    /// <summary>Escrow refunded to the buyer after a dispute resolution / timeout.</summary>
    Refunded = 4,
    /// <summary>Seller cancelled the pending order; escrow refunded to the buyer (no fee).</summary>
    Cancelled = 5,
    /// <summary>A buyer's price offer: funds are held (binding) awaiting the seller's accept/decline.</summary>
    OfferPending = 6,
    /// <summary>The offer was declined / withdrawn / expired — funds refunded to the buyer.</summary>
    OfferDeclined = 7,
}

/// <summary>Lifecycle of a price offer / counteroffer on a listing.</summary>
public enum ShopOfferStatus
{
    /// <summary>Standing offer awaiting the other party's response.</summary>
    Proposed = 0,
    /// <summary>Superseded by a counter from the other party.</summary>
    Countered = 1,
    /// <summary>Accepted — converted into an order.</summary>
    Accepted = 2,
    /// <summary>Declined by the responder.</summary>
    Rejected = 3,
    /// <summary>Pulled by the proposer before a response.</summary>
    Withdrawn = 4,
    /// <summary>Lapsed past its expiry without a response.</summary>
    Expired = 5,
}

/// <summary>Which side proposed an offer. Buyer-proposed offers escrow funds at proposal time (binding).</summary>
public enum ShopOfferParty
{
    Buyer = 0,
    Seller = 1,
}

/// <summary>What a shop rating is about. Kept deliberately generic so a future reputation layer can reuse it
/// across features (e.g. quest worker/poster ratings). Superseded by the generic <see cref="RatingContext"/> +
/// <see cref="RatingRole"/> model; retained only for source compatibility.</summary>
public enum ShopRatingSubject
{
    Store = 0,
    Seller = 1,
    Buyer = 2,
}

// --- Reputation / ratings (generic across features) ---

/// <summary>The feature a reputation rating came out of. The rating row carries this + the feature's row id
/// (<c>SourceId</c>) so one reputation table serves the shop now and quests later without per-feature joins.</summary>
public enum RatingContext
{
    /// <summary>A shop order (<c>SourceId</c> = the order id). Buyer rates the seller, seller rates the buyer.</summary>
    ShopOrder = 0,

    /// <summary>A quest (<c>SourceId</c> = the quest/participant id). Reserved — wired in a later pass.</summary>
    Quest = 1,
}

/// <summary>The rated party's role in the transaction, denormalized onto the rating so aggregates ("seller
/// reputation", "buyer reputation") never touch the feature tables. Provider = the one who delivers (seller /
/// quest worker); Consumer = the one who pays/poses (buyer / quest poster).</summary>
public enum RatingRole
{
    Provider = 0,
    Consumer = 1,
}

/// <summary>A point in a shop listing/order's lifecycle worth notifying someone about (Discord DM/post, API).</summary>
public enum ShopLifecycleMoment
{
    Listed,
    OfferMade,
    OfferCountered,
    OfferAccepted,
    OfferRejected,
    Purchased,
    Delivered,
    Confirmed,
    Settled,
    Disputed,
    Arbitrated,
    Refunded,
    Cancelled,
    Expired,
    Rated,
    Featured,
    Unfeatured,
}
