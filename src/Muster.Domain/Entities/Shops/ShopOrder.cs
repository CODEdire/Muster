using Muster.Contracts;

namespace Muster.Domain.Entities.Shops;

/// <summary>
/// A buyer's escrowed purchase of a listing. On purchase the buyer's funds are held in escrow; on
/// confirmation they settle to the seller (less a burned commission). The inverse of a player bounty.
/// Dispute + rating fields are populated in later phases.
/// </summary>
public class ShopOrder
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public Guid ListingId { get; set; }

    /// <summary>The store this order belongs to, captured at purchase so it survives the listing being deleted
    /// (powers per-store order filtering + the store column on the orders page).</summary>
    public Guid StoreId { get; set; }

    /// <summary>The offer this order was created from (null for a direct buy-now). Set in the offers phase.</summary>
    public Guid? OfferId { get; set; }

    public ulong BuyerId { get; set; }
    public ulong SellerId { get; set; }

    /// <summary>Snapshot of the store's origin at purchase. A <see cref="ShopStoreOrigin.Guild"/> order settles by
    /// burning the buyer's payment (consume) instead of paying the seller; member orders pay the seller as usual.</summary>
    public ShopStoreOrigin Origin { get; set; } = ShopStoreOrigin.Member;

    /// <summary>The listing's name captured at purchase, so receipts/history survive later edits/deletes.</summary>
    public string ItemNameSnapshot { get; set; } = string.Empty;

    public Guid CurrencyId { get; set; }

    /// <summary>Total held in escrow for this order (unit price × quantity).</summary>
    public long Amount { get; set; }

    /// <summary>Commission burned on settlement (0 until settled / when no commission applies).</summary>
    public long FeeAmount { get; set; }

    public int Quantity { get; set; } = 1;

    /// <summary>True when this order began as a buyer price offer (vs a direct buy-now).</summary>
    public bool FromOffer { get; set; }

    /// <summary>For an offer in negotiation (OfferPending): who proposed the current <see cref="Amount"/>. A
    /// buyer-proposed price has the funds held (seller's turn); a seller counter releases the hold (buyer's turn).</summary>
    public ShopOfferParty OfferProposedBy { get; set; } = ShopOfferParty.Buyer;

    /// <summary>Counter-negotiation round, bumped each time a held offer is released so the next hold/refund uses a
    /// fresh escrow key (the ledger is idempotent per source id — reusing a key would silently no-op).</summary>
    public int OfferRound { get; set; }

    public ShopOrderStatus Status { get; set; } = ShopOrderStatus.PendingDelivery;

    /// <summary>When the order last changed status — drives auto-settle + dispute-timeout sweeps.</summary>
    public DateTimeOffset StatusChangedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }

    // --- Dispute (phase 3) ---
    public ulong? DisputedBy { get; set; }
    public string? DisputeReason { get; set; }
    public string? DisputeEvidenceKey { get; set; }
    public ulong? ResolvedBy { get; set; }

    // --- Ratings (phase 6) ---

    /// <summary>When the blind-rating window shuts and any pending ratings auto-reveal (set on settle; null when
    /// the guild runs no window). Drives the reveal sweep + the receipt's "rate by" hint.</summary>
    public DateTimeOffset? RatingWindowClosesAt { get; set; }

    /// <summary>True once the window has been processed (ratings revealed) or both parties rated — bars further
    /// rating and stops the reveal sweep re-scanning a settled order forever.</summary>
    public bool RatingsClosed { get; set; }

    /// <summary>Optimistic-concurrency token (SQL Server only).</summary>
    public byte[]? RowVersion { get; set; }

    public ShopListing? Listing { get; set; }

    public static ShopOrder Create(ShopListing listing, ulong buyerId, int quantity, long amount)
        => New(listing, buyerId, quantity, amount, ShopOrderStatus.PendingDelivery, fromOffer: false);

    /// <summary>A buyer price offer: the requested <paramref name="amount"/> is held (binding) awaiting the seller.</summary>
    public static ShopOrder CreateOffer(ShopListing listing, ulong buyerId, int quantity, long amount)
        => New(listing, buyerId, quantity, amount, ShopOrderStatus.OfferPending, fromOffer: true);

    private static ShopOrder New(ShopListing listing, ulong buyerId, int quantity, long amount, ShopOrderStatus status, bool fromOffer)
    {
        var now = DateTimeOffset.UtcNow;
        return new ShopOrder
        {
            Id = Guid.NewGuid(),
            GuildId = listing.GuildId,
            ListingId = listing.Id,
            StoreId = listing.StoreId,
            BuyerId = buyerId,
            SellerId = listing.SellerId,
            Origin = listing.Store?.Origin ?? ShopStoreOrigin.Member,
            ItemNameSnapshot = listing.Name,
            CurrencyId = listing.CurrencyId,
            Amount = amount,
            Quantity = quantity,
            FromOffer = fromOffer,
            Status = status,
            StatusChangedAt = now,
            CreatedAt = now,
        };
    }

    /// <summary>Move to a new status, keeping <see cref="StatusChangedAt"/> in lock-step. Idempotent; throws if
    /// already terminal (Settled/Refunded/Cancelled).</summary>
    public void TransitionTo(ShopOrderStatus next)
    {
        if (Status == next)
        {
            return;
        }

        if (Status is ShopOrderStatus.Settled or ShopOrderStatus.Refunded or ShopOrderStatus.Cancelled or ShopOrderStatus.OfferDeclined)
        {
            throw new InvalidOperationException($"Order {Id} is in terminal state {Status}; cannot transition to {next}.");
        }

        Status = next;
        StatusChangedAt = DateTimeOffset.UtcNow;
    }
}
