using Muster.Contracts;

namespace Muster.Domain.Entities.Shops;

/// <summary>The inputs to create a listing. Shared by every surface (web/bot/API).</summary>
public record ShopListingDraft(
    ulong GuildId,
    Guid StoreId,
    ulong SellerId,
    string Name,
    string Description,
    Guid CurrencyId,
    long Price,
    Guid? CategoryId = null,
    int Quantity = 1,
    bool AcceptsOffers = true,
    string? ImageKey = null,
    string? ThumbKey = null,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// A sell offer for an in-game item: the seller names an ask <see cref="Price"/> in some spendable currency,
/// a buyer pays into escrow, and the funds settle to the seller once the buyer confirms receipt. The inverse of
/// a player bounty (which holds the poster's money and pays the worker). Orders/offers hang off this aggregate.
/// </summary>
public class ShopListing
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public Guid StoreId { get; set; }
    public ulong SellerId { get; set; }

    public Guid? CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Blob key for the primary image (null = none).</summary>
    public string? ImageKey { get; set; }
    /// <summary>Blob key for the generated thumbnail shown on the board (null = none).</summary>
    public string? ThumbKey { get; set; }

    public Guid CurrencyId { get; set; }

    /// <summary>Ask price per unit, in the currency's minor units.</summary>
    public long Price { get; set; }

    /// <summary>Units available. 1 for a single-item listing; decremented as stackable units sell.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Whether buyers may make price offers on this listing (in addition to buy-now). Gated by the
    /// guild's <c>OffersEnabled</c> too — both must be on. Sellers toggle this per listing.</summary>
    public bool AcceptsOffers { get; set; } = true;

    public ShopListingStatus Status { get; set; } = ShopListingStatus.Active;

    /// <summary>When the listing last changed status — drives expiry/cleanup sweeps.</summary>
    public DateTimeOffset StatusChangedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Auto-delist time (null = never expires).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Promoted-until time: while in the future the listing is "featured" — sorted to the top of browse
    /// and shown as a card in the shop channel. Null / past = not featured. Set by a paid feature purchase.</summary>
    public DateTimeOffset? FeaturedUntil { get; set; }

    /// <summary>Who delisted it (on cancel/takedown). Equal to <see cref="SellerId"/> = a self-withdrawal; anyone
    /// else = a moderator takedown. Null until delisted.</summary>
    public ulong? DelistedBy { get; set; }

    /// <summary>Why it was delisted — shown to the seller, especially for a moderator takedown. Null = none given.</summary>
    public string? DelistReason { get; set; }

    /// <summary>Optimistic-concurrency token (SQL Server only). Guards the last-unit purchase race.</summary>
    public byte[]? RowVersion { get; set; }

    public ShopStore? Store { get; set; }
    public List<ShopListingTag> Tags { get; set; } = [];

    public static ShopListing Create(ShopListingDraft draft)
    {
        var now = DateTimeOffset.UtcNow;
        var listing = new ShopListing
        {
            Id = Guid.NewGuid(),
            GuildId = draft.GuildId,
            StoreId = draft.StoreId,
            SellerId = draft.SellerId,
            CategoryId = draft.CategoryId,
            Name = draft.Name.Trim(),
            Description = (draft.Description ?? string.Empty).Trim(),
            ImageKey = draft.ImageKey,
            ThumbKey = draft.ThumbKey,
            CurrencyId = draft.CurrencyId,
            Price = draft.Price,
            Quantity = Math.Max(1, draft.Quantity),
            AcceptsOffers = draft.AcceptsOffers,
            Status = ShopListingStatus.Active,
            StatusChangedAt = now,
            CreatedAt = now,
            ExpiresAt = draft.ExpiresAt,
        };

        foreach (var tag in ShopListingTag.Normalize(draft.Tags))
        {
            listing.Tags.Add(new ShopListingTag { ListingId = listing.Id, Tag = tag });
        }

        return listing;
    }

    /// <summary>Move to a new status, keeping <see cref="StatusChangedAt"/> in lock-step. Idempotent; throws if
    /// already terminal (Cancelled/Expired). Callers enforce which transitions are allowed.</summary>
    public void TransitionTo(ShopListingStatus next)
    {
        if (Status == next)
        {
            return;
        }

        if (Status is ShopListingStatus.Cancelled or ShopListingStatus.Expired)
        {
            throw new InvalidOperationException(
                $"Listing {Id} is in terminal state {Status}; cannot transition to {next}.");
        }

        Status = next;
        StatusChangedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>A free-form, player-applied tag on a listing (normalised: lower-cased, trimmed, deduped). Many per
/// listing; backs tag-based market search.</summary>
public class ShopListingTag
{
    public Guid ListingId { get; set; }
    public string Tag { get; set; } = string.Empty;

    public ShopListing? Listing { get; set; }

    /// <summary>Normalise a raw tag list: trim, lower-case, drop blanks, dedupe (preserving order).</summary>
    public static IEnumerable<string> Normalize(IReadOnlyList<string>? raw)
    {
        if (raw is null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in raw)
        {
            var norm = t?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
            {
                yield return norm;
            }
        }
    }
}
