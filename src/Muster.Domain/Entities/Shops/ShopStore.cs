using Muster.Domain.Entities.Guilds;

namespace Muster.Domain.Entities.Shops;

/// <summary>
/// A named storefront a seller runs inside one guild's marketplace. A seller may run several (e.g. one for
/// resources, one for armor); every <see cref="ShopListing"/> belongs to exactly one store. The global market
/// page lists listings across all stores; a storefront page filters to a single store by <see cref="Slug"/>.
/// Opening/listing requires the <c>ShopCreator</c> role — enforced in the authorizer, not here.
/// </summary>
public class ShopStore
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>The member who owns (and manages) this store.</summary>
    public ulong OwnerId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>URL-friendly handle, unique within the guild — drives the storefront route.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>The store's "type" — an admin-curated <see cref="ShopStoreType"/> (like a category, but for stores),
    /// shown on the hero card. Null = none. Owner or a shop manager picks from the guild's list.</summary>
    public Guid? StoreTypeId { get; set; }

    /// <summary>Blob key for the storefront banner image — a wide header (null = none). Bytes live in storage.</summary>
    public string? BannerImageKey { get; set; }

    /// <summary>Blob key for the store's square logo, shown on cards/grids (null = none → initials/accent fallback).</summary>
    public string? LogoImageKey { get; set; }

    /// <summary>Optional hex accent colour for the storefront (e.g. <c>#7c3aed</c>).</summary>
    public string? AccentColor { get; set; }

    /// <summary>When true, the store is closed: its listings are delisted and it no longer accepts new ones.
    /// Closing requires open orders/offers to be settled or refunded first.</summary>
    public bool Closed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (SQL Server only; tests' in-memory provider skips it).</summary>
    public byte[]? RowVersion { get; set; }

    public Guild? Guild { get; set; }

    public static ShopStore Create(ulong guildId, ulong ownerId, string name, string slug, string? description)
        => new()
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            OwnerId = ownerId,
            Name = name.Trim(),
            Slug = slug.Trim().ToLowerInvariant(),
            Description = (description ?? string.Empty).Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
}

/// <summary>
/// A guild-curated <b>store</b> type (controlled vocabulary, admin-managed) — like a category, but classifies a
/// whole storefront (e.g. "Weapons", "Resources"). Shown on the store's hero card.
/// </summary>
public class ShopStoreType
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Display order on pickers (ascending).</summary>
    public int Sort { get; set; }

    /// <summary>A curated icon key (see <c>Muster.Contracts.ShopIcons</c>). Null = none. Stands in for a store's
    /// logo on the UI when none is set.</summary>
    public string? Icon { get; set; }

    public Guild? Guild { get; set; }

    public static ShopStoreType Create(ulong guildId, string name, int sort, string? icon = null)
        => new() { Id = Guid.NewGuid(), GuildId = guildId, Name = name.Trim(), Sort = sort, Icon = icon };
}

/// <summary>
/// A guild-curated listing category (controlled vocabulary, admin-managed). One per listing. Lets the market
/// page filter by category and supports a per-category commission override.
/// </summary>
public class ShopCategory
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Display order on pickers/filters (ascending).</summary>
    public int Sort { get; set; }

    /// <summary>When set, overrides the guild's default commission rate (basis points) for listings in this
    /// category. Null = use <c>GuildShopSettings.CommissionBps</c>.</summary>
    public int? CommissionBpsOverride { get; set; }

    /// <summary>A curated icon key (see <c>Muster.Contracts.ShopIcons</c>). Null = none. Shown as the category badge.</summary>
    public string? Icon { get; set; }

    public Guild? Guild { get; set; }

    public static ShopCategory Create(ulong guildId, string name, int sort, int? commissionBpsOverride, string? icon = null)
        => new()
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Name = name.Trim(),
            Sort = sort,
            CommissionBpsOverride = commissionBpsOverride,
            Icon = icon,
        };
}
