using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>A listing as the market/storefront board needs it (names + currency code resolved, no entity leaks).</summary>
public record ShopListingCard(
    Guid Id, string Name, string Description, long Price, string CurrencyCode, string? ThumbKey,
    Guid StoreId, string StoreName, string StoreSlug, ulong SellerId, string SellerName,
    Guid? CategoryId, string? CategoryName, IReadOnlyList<string> Tags,
    ShopListingStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, int Quantity = 1, bool Featured = false,
    string? SellerAvatarUrl = null, string? CategoryIcon = null)
{
    /// <summary>Listed within the last 48h — drives the "NEW" tile chip.</summary>
    public bool IsNew => CreatedAt > DateTimeOffset.UtcNow.AddHours(-48);
}

/// <summary>A paged market board.</summary>
public record ShopBoardPage(IReadOnlyList<ShopListingCard> Items, int Total, int Page, int TotalPages);

/// <summary>A storefront header + its live listings. The owner's seller reputation is resolved for the header.</summary>
public record ShopStorefront(
    Guid Id, string Name, string Slug, string Description, string? BannerImageKey, string? LogoImageKey, string? AccentColor,
    ulong OwnerId, string OwnerName, bool Closed, IReadOnlyList<ShopListingCard> Listings,
    double SellerRatingAvg, int SellerRatingCount, string? StoreTypeName = null, string? StoreTypeIcon = null,
    string? OwnerAvatarUrl = null);

/// <summary>The full listing for the detail page.</summary>
public record ShopListingDetail(
    Guid Id, string Name, string Description, long Price, Guid CurrencyId, string CurrencyCode, string? ImageKey,
    int Quantity, ShopListingStatus Status, Guid StoreId, string StoreName, string StoreSlug,
    ulong SellerId, string SellerName, Guid? CategoryId, string? CategoryName, IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool OffersOpen, DateTimeOffset? FeaturedUntil = null,
    ulong? DelistedBy = null, string? DelistReason = null, string? CategoryIcon = null, string? SellerAvatarUrl = null)
{
    public bool Featured => FeaturedUntil is { } f && f > DateTimeOffset.UtcNow;

    /// <summary>Delisted by a moderator (vs the seller's own withdrawal).</summary>
    public bool Moderated => DelistedBy is { } d && d != SellerId;
}

/// <summary>A store row for the Shops tab (grid + tiles): owner name + listing count + images resolved.</summary>
public record ShopStoreRow(
    Guid Id, string Name, string Slug, ulong OwnerId, string OwnerName, bool Closed, int ListingCount,
    string? LogoImageKey, string? BannerImageKey, string? AccentColor, DateTimeOffset CreatedAt,
    string? OwnerAvatarUrl = null, string? Description = null, string? StoreTypeName = null, string? StoreTypeIcon = null,
    double RatingAvg = 0, int RatingCount = 0, IReadOnlyList<ShopStoreFeaturedItem>? Featured = null);

/// <summary>A featured listing as shown inline on a shop card (Discord-style preview strip).</summary>
public record ShopStoreFeaturedItem(Guid Id, string Name, long Price, string CurrencyCode);

/// <summary>A paged store list (Shops tab).</summary>
public record ShopStorePage(IReadOnlyList<ShopStoreRow> Items, int Total, int Page, int TotalPages);

/// <summary>A store's editable config (the Config tab on the detail page).</summary>
public record ShopStoreDetail(
    Guid Id, string Name, string Slug, string Description, string? BannerImageKey, string? LogoImageKey,
    string? AccentColor, ulong OwnerId, string OwnerName, bool Closed, Guid? StoreTypeId = null,
    ShopStoreOrigin Origin = ShopStoreOrigin.Member);

/// <summary>A listing row for a store's management tab — every status, not just active.</summary>
public record ShopManageListingRow(
    Guid Id, string Name, long Price, string CurrencyCode, ShopListingStatus Status, int Quantity,
    string? ThumbKey, string? CategoryName, DateTimeOffset CreatedAt, bool Moderated = false, string? DelistReason = null,
    string? CategoryIcon = null, IReadOnlyList<string>? Tags = null, bool Featured = false);

/// <summary>A paged management-listings result for a store's Listings tab.</summary>
public record ShopManageListingPage(IReadOnlyList<ShopManageListingRow> Items, int Total, int Page, int TotalPages);

/// <summary>An order row for the my-orders / sales surfaces (counterparty + currency resolved).</summary>
public record ShopOrderRow(
    Guid Id, Guid ListingId, string ItemName, long Amount, long FeeAmount, string CurrencyCode, int Quantity,
    ShopOrderStatus Status, ShopOfferParty OfferProposedBy, ulong BuyerId, string BuyerName, ulong SellerId, string SellerName, DateTimeOffset CreatedAt,
    Guid? StoreId = null, string? StoreName = null,
    string? BuyerAvatarUrl = null, string? SellerAvatarUrl = null,
    string? ThumbKey = null, string? CategoryName = null, string? CategoryIcon = null,
    DateTimeOffset? OfferExpiresAt = null);

/// <summary>The full receipt for one order — the canonical, stable transaction record (item name is snapshotted
/// so it survives listing edits/deletes).</summary>
public record ShopOrderDetail(
    Guid Id, Guid ListingId, bool ListingAvailable, string ItemName, int Quantity,
    long Amount, long FeeAmount, string CurrencyCode, ShopOrderStatus Status,
    ulong BuyerId, string BuyerName, ulong SellerId, string SellerName,
    DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt, DateTimeOffset? ConfirmedAt, DateTimeOffset? SettledAt,
    DateTimeOffset? RatingWindowClosesAt, ulong? DisputedBy, string? DisputeReason, string? DisputeEvidenceKey,
    ShopOfferParty OfferProposedBy, bool TwoStepDelivery, bool RatingsEnabled, bool RatingsClosed,
    double SellerRatingAvg = 0, int SellerRatingCount = 0, double BuyerRatingAvg = 0, int BuyerRatingCount = 0,
    Guid? StoreId = null, string? StoreName = null, string? StoreSlug = null,
    string? ThumbKey = null, string? CategoryName = null, string? CategoryIcon = null,
    string? BuyerAvatarUrl = null, string? SellerAvatarUrl = null, DateTimeOffset? AutoSettleAt = null,
    DateTimeOffset? OfferExpiresAt = null, DateTimeOffset? DisputeRaisedAt = null, DateTimeOffset? DisputeAutoResolveAt = null,
    long MinPrice = 0, long MaxPrice = 0)
{
    public long Net => Amount - FeeAmount;
    public long UnitPrice => Amount / Math.Max(1, Quantity);
}

/// <summary>One rating as a viewer may see it. Comment is null while the rating is still blind to the viewer.</summary>
public record ShopRatingView(
    Guid Id, ulong RaterId, string RaterName, ulong SubjectId, RatingRole Role, int Stars, string? Comment,
    bool Hidden, bool Moderated, DateTimeOffset CreatedAt);

/// <summary>The rating state of one order for a given viewer: what they can see + whether they've rated yet.</summary>
public record ShopOrderRatings(bool BothRated, ShopRatingView? Mine, ShopRatingView? Counterparty);

/// <summary>The fields the listing edit form prefills from.</summary>
public record ShopListingEditView(
    Guid StoreId, string Name, string Description, long Price, string CurrencyCode, Guid? CategoryId,
    int Quantity, bool AcceptsOffers, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Tags, string? ImageKey);

/// <summary>
/// Read side of the shop (CQRS-lite: commands go through the bus, reads through this interface). Returns DTOs so
/// adapters never touch <see cref="MusterDbContext"/> or tracked entities. (Phase 1: market/storefront/detail/edit.)
/// </summary>
public interface IShopReadService
{
    Task<ShopBoardPage> GetMarketAsync(
        ulong guildId, Guid? categoryId, string? tag, string? search, string sort, bool desc,
        int page, int pageSize, CancellationToken ct = default, Guid? storeId = null,
        long? minPrice = null, long? maxPrice = null, bool inStockOnly = false, Guid? currencyId = null);

    /// <summary>A store's currently-featured, active listings (for its hero card).</summary>
    Task<IReadOnlyList<ShopListingCard>> GetFeaturedForStoreAsync(ulong guildId, Guid storeId, CancellationToken ct = default);

    Task<ShopStorefront?> GetStorefrontAsync(ulong guildId, string slug, CancellationToken ct = default);

    Task<ShopListingDetail?> GetListingDetailAsync(ulong guildId, Guid listingId, CancellationToken ct = default);

    Task<ShopListingEditView?> GetForEditAsync(ulong guildId, Guid listingId, CancellationToken ct = default);

    Task<IReadOnlyList<ShopStore>> GetStoresByOwnerAsync(ulong guildId, ulong ownerId, CancellationToken ct = default);

    /// <summary>Paged/sorted/filtered store grid. <paramref name="ownerScope"/> null = all stores (shop managers);
    /// a user id = only that owner's stores (shop owners manage their own).</summary>
    Task<ShopStorePage> GetStoresAsync(
        ulong guildId, ulong? ownerScope, bool includeClosed, string? search, string sort, bool desc, int page, int pageSize,
        CancellationToken ct = default, Guid? storeTypeId = null);

    /// <summary>A single store's editable config, or null if not found.</summary>
    Task<ShopStoreDetail?> GetStoreAsync(ulong guildId, Guid storeId, CancellationToken ct = default);

    /// <summary>Every listing in a store (all statuses) for the management tab.</summary>
    Task<ShopManageListingPage> GetStoreManageListingsAsync(
        ulong guildId, Guid storeId, ShopListingStatus? status, string? search, string sort, bool desc,
        int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<ShopCategory>> GetCategoriesAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>The guild's admin-managed store types (for the store-config picker + admin page).</summary>
    Task<IReadOnlyList<ShopStoreType>> GetStoreTypesAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>Spendable currencies the marketplace may price in (for the post/edit currency picker).</summary>
    Task<IReadOnlyList<ShopCurrencyChoice>> GetSpendableCurrenciesAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>Orders the user placed as a buyer (my purchases).</summary>
    Task<IReadOnlyList<ShopOrderRow>> GetPurchasesAsync(ulong guildId, ulong buyerId, Guid? storeId = null, CancellationToken ct = default);

    /// <summary>Orders the user received as a seller (my sales). Optionally filtered to one store.</summary>
    Task<IReadOnlyList<ShopOrderRow>> GetSalesAsync(ulong guildId, ulong sellerId, Guid? storeId = null, CancellationToken ct = default);

    /// <summary>The full receipt for one order, or null if not found.</summary>
    Task<ShopOrderDetail?> GetOrderAsync(ulong guildId, Guid orderId, CancellationToken ct = default);

    /// <summary>The shop manager's arbitration queue — disputed orders (oldest first). Optionally scoped to one store.</summary>
    Task<IReadOnlyList<ShopOrderRow>> GetDisputesAsync(ulong guildId, Guid? storeId = null, CancellationToken ct = default);

    /// <summary>An order's rating state for <paramref name="viewerId"/>: their own rating (always visible to them)
    /// + the counterparty's once revealed. Blind ratings stay hidden until the mutual reveal.</summary>
    Task<ShopOrderRatings> GetOrderRatingsAsync(ulong guildId, Guid orderId, ulong viewerId, CancellationToken ct = default);
}

/// <summary>A spendable currency option for the listing forms.</summary>
public record ShopCurrencyChoice(Guid Id, string Code, string Name);

public sealed class ShopReadService(MusterDbContext db) : IShopReadService
{
    public async Task<ShopBoardPage> GetMarketAsync(
        ulong guildId, Guid? categoryId, string? tag, string? search, string sort, bool desc,
        int page, int pageSize, CancellationToken ct = default, Guid? storeId = null,
        long? minPrice = null, long? maxPrice = null, bool inStockOnly = false, Guid? currencyId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.ShopListings.AsNoTracking()
            .Where(l => l.GuildId == guildId && l.Status == ShopListingStatus.Active);

        if (storeId is { } sid)
        {
            query = query.Where(l => l.StoreId == sid);
        }

        if (categoryId is { } cid)
        {
            query = query.Where(l => l.CategoryId == cid);
        }

        if (currencyId is { } curr)
        {
            query = query.Where(l => l.CurrencyId == curr);
        }

        if (minPrice is { } min)
        {
            query = query.Where(l => l.Price >= min);
        }

        if (maxPrice is { } max)
        {
            query = query.Where(l => l.Price <= max);
        }

        if (inStockOnly)
        {
            query = query.Where(l => l.Quantity > 0);
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var norm = tag.Trim().ToLowerInvariant();
            query = query.Where(l => l.Tags.Any(t => t.Tag == norm));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(l => EF.Functions.Like(l.Name, $"%{term}%") || EF.Functions.Like(l.Description, $"%{term}%"));
        }

        // Featured listings (active feature window) always sort to the top, then the chosen order.
        var now = DateTimeOffset.UtcNow;
        var ranked = query.OrderByDescending(l => l.FeaturedUntil != null && l.FeaturedUntil > now);
        query = (sort, desc) switch
        {
            ("price", false) => ranked.ThenBy(l => l.Price),
            ("price", true) => ranked.ThenByDescending(l => l.Price),
            ("name", false) => ranked.ThenBy(l => l.Name),
            ("name", true) => ranked.ThenByDescending(l => l.Name),
            ("stock", false) => ranked.ThenBy(l => l.Quantity),
            ("stock", true) => ranked.ThenByDescending(l => l.Quantity),
            (_, false) => ranked.ThenBy(l => l.CreatedAt),
            _ => ranked.ThenByDescending(l => l.CreatedAt),
        };

        var total = await query.CountAsync(ct);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Include(l => l.Tags).Include(l => l.Store).ToListAsync(ct);

        var cards = await ToCardsAsync(guildId, rows, ct);
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return new ShopBoardPage(cards, total, page, totalPages);
    }

    public async Task<IReadOnlyList<ShopListingCard>> GetFeaturedForStoreAsync(ulong guildId, Guid storeId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ShopListings.AsNoTracking()
            .Where(l => l.GuildId == guildId && l.StoreId == storeId && l.Status == ShopListingStatus.Active
                && l.FeaturedUntil != null && l.FeaturedUntil > now)
            .Include(l => l.Tags).Include(l => l.Store)
            .OrderByDescending(l => l.FeaturedUntil).ToListAsync(ct);
        return await ToCardsAsync(guildId, rows, ct);
    }

    public async Task<ShopStorefront?> GetStorefrontAsync(ulong guildId, string slug, CancellationToken ct = default)
    {
        var store = await db.FindStoreBySlugAsync(guildId, slug, ct);
        if (store is null)
        {
            return null;
        }

        var rows = await db.ListByStoreAsync(store.Id, ct);
        var cards = await ToCardsAsync(guildId, rows, ct);
        var names = await NamesAsync([store.OwnerId], ct);
        var avatars = await AvatarsAsync([store.OwnerId], ct);
        var (avg, count) = await db.RatingSummaryAsync(guildId, RatingContext.ShopOrder, store.OwnerId, RatingRole.Provider, ct);
        var type = store.StoreTypeId is { } tid ? await db.FindStoreTypeInGuildAsync(guildId, tid, ct) : null;

        return new ShopStorefront(
            store.Id, store.Name, store.Slug, store.Description, store.BannerImageKey, store.LogoImageKey, store.AccentColor,
            store.OwnerId, names.GetValueOrDefault(store.OwnerId, store.OwnerId.ToString()), store.Closed, cards, avg, count,
            type?.Name, type?.Icon, avatars.GetValueOrDefault(store.OwnerId));
    }

    public async Task<ShopListingDetail?> GetListingDetailAsync(ulong guildId, Guid listingId, CancellationToken ct = default)
    {
        var l = await db.ShopListings.AsNoTracking().Include(x => x.Tags).Include(x => x.Store)
            .FirstOrDefaultAsync(x => x.Id == listingId && x.GuildId == guildId, ct);
        if (l is null)
        {
            return null;
        }

        var code = await CurrencyCodeAsync(guildId, l.CurrencyId, ct);
        var category = l.CategoryId is { } cid ? await db.FindCategoryInGuildAsync(guildId, cid, ct) : null;
        var names = await NamesAsync([l.SellerId], ct);
        var avatars = await AvatarsAsync([l.SellerId], ct);
        var offersEnabledGuild = (await db.GuildShopSettings.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct))?.OffersEnabled ?? true;

        return new ShopListingDetail(
            l.Id, l.Name, l.Description, l.Price, l.CurrencyId, code, l.ImageKey, l.Quantity, l.Status,
            l.StoreId, l.Store?.Name ?? string.Empty, l.Store?.Slug ?? string.Empty,
            l.SellerId, names.GetValueOrDefault(l.SellerId, l.SellerId.ToString()),
            l.CategoryId, category?.Name, l.Tags.Select(t => t.Tag).ToList(), l.CreatedAt, l.ExpiresAt,
            OffersOpen: offersEnabledGuild && l.AcceptsOffers, FeaturedUntil: l.FeaturedUntil,
            DelistedBy: l.DelistedBy, DelistReason: l.DelistReason, CategoryIcon: category?.Icon,
            SellerAvatarUrl: avatars.GetValueOrDefault(l.SellerId));
    }

    public async Task<ShopListingEditView?> GetForEditAsync(ulong guildId, Guid listingId, CancellationToken ct = default)
    {
        var l = await db.ShopListings.AsNoTracking().Include(x => x.Tags)
            .FirstOrDefaultAsync(x => x.Id == listingId && x.GuildId == guildId, ct);
        if (l is null)
        {
            return null;
        }

        var code = await CurrencyCodeAsync(guildId, l.CurrencyId, ct);
        return new ShopListingEditView(
            l.StoreId, l.Name, l.Description, l.Price, code, l.CategoryId, l.Quantity, l.AcceptsOffers, l.ExpiresAt,
            l.Tags.Select(t => t.Tag).ToList(), l.ImageKey);
    }

    public async Task<IReadOnlyList<ShopStore>> GetStoresByOwnerAsync(ulong guildId, ulong ownerId, CancellationToken ct = default)
        => await db.ShopStores.AsNoTracking().Where(s => s.GuildId == guildId && s.OwnerId == ownerId)
            .OrderBy(s => s.Name).ToListAsync(ct);

    public async Task<ShopStorePage> GetStoresAsync(
        ulong guildId, ulong? ownerScope, bool includeClosed, string? search, string sort, bool desc, int page, int pageSize,
        CancellationToken ct = default, Guid? storeTypeId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.ShopStores.AsNoTracking().Where(s => s.GuildId == guildId);
        if (ownerScope is { } owner)
        {
            query = query.Where(s => s.OwnerId == owner);
        }

        if (storeTypeId is { } stid)
        {
            query = query.Where(s => s.StoreTypeId == stid);
        }

        if (!includeClosed)
        {
            query = query.Where(s => !s.Closed);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(s => EF.Functions.Like(s.Name, $"%{term}%") || EF.Functions.Like(s.Slug, $"%{term}%"));
        }

        query = (sort, desc) switch
        {
            ("name", false) => query.OrderBy(s => s.Name),
            ("name", true) => query.OrderByDescending(s => s.Name),
            (_, false) => query.OrderBy(s => s.CreatedAt),
            _ => query.OrderByDescending(s => s.CreatedAt),
        };

        var total = await query.CountAsync(ct);
        var stores = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var ids = stores.Select(s => s.Id).ToList();
        var counts = await db.ShopListings.AsNoTracking()
            .Where(l => ids.Contains(l.StoreId) && l.Status == ShopListingStatus.Active)
            .GroupBy(l => l.StoreId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var ownerIds = stores.Select(s => s.OwnerId).Distinct().ToList();
        var names = await NamesAsync(ownerIds, ct);
        var avatars = await AvatarsAsync(ownerIds, ct);

        // Store types (batched).
        var typeIds = stores.Where(s => s.StoreTypeId is not null).Select(s => s.StoreTypeId!.Value).Distinct().ToList();
        var types = typeIds.Count == 0
            ? new Dictionary<Guid, (string Name, string? Icon)>()
            : await db.ShopStoreTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => (t.Name, t.Icon), ct);

        // Top featured items per store (batched): pull the page's active featured listings, group, take a few each.
        var now = DateTimeOffset.UtcNow;
        var currencyCodes = await db.Currencies.AsNoTracking().Where(c => c.GuildId == guildId)
            .ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var featuredRows = await db.ShopListings.AsNoTracking()
            .Where(l => ids.Contains(l.StoreId) && l.Status == ShopListingStatus.Active
                && l.FeaturedUntil != null && l.FeaturedUntil > now)
            .OrderByDescending(l => l.FeaturedUntil)
            .Select(l => new { l.Id, l.StoreId, l.Name, l.Price, l.CurrencyId })
            .ToListAsync(ct);
        var featuredByStore = featuredRows
            .GroupBy(l => l.StoreId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ShopStoreFeaturedItem>)g.Take(5)
                .Select(l => new ShopStoreFeaturedItem(l.Id, l.Name, l.Price, currencyCodes.GetValueOrDefault(l.CurrencyId, "?")))
                .ToList());

        // Seller rating per owner (distinct owners → one summary each).
        var ratings = new Dictionary<ulong, (double Avg, int Count)>();
        foreach (var ownerId in ownerIds)
        {
            ratings[ownerId] = await db.RatingSummaryAsync(guildId, RatingContext.ShopOrder, ownerId, RatingRole.Provider, ct);
        }

        var rows = stores.Select(s =>
        {
            var (rAvg, rCount) = ratings.GetValueOrDefault(s.OwnerId);
            var type = s.StoreTypeId is { } tid && types.TryGetValue(tid, out var t) ? t : default;
            return new ShopStoreRow(
                s.Id, s.Name, s.Slug, s.OwnerId, names.GetValueOrDefault(s.OwnerId, s.OwnerId.ToString()),
                s.Closed, counts.GetValueOrDefault(s.Id), s.LogoImageKey, s.BannerImageKey, s.AccentColor, s.CreatedAt,
                avatars.GetValueOrDefault(s.OwnerId), s.Description, type.Name, type.Icon,
                rAvg, rCount, featuredByStore.GetValueOrDefault(s.Id));
        }).ToList();

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return new ShopStorePage(rows, total, page, totalPages);
    }

    public async Task<ShopStoreDetail?> GetStoreAsync(ulong guildId, Guid storeId, CancellationToken ct = default)
    {
        var s = await db.ShopStores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == storeId && x.GuildId == guildId, ct);
        if (s is null)
        {
            return null;
        }

        var names = await NamesAsync([s.OwnerId], ct);
        return new ShopStoreDetail(
            s.Id, s.Name, s.Slug, s.Description, s.BannerImageKey, s.LogoImageKey, s.AccentColor,
            s.OwnerId, names.GetValueOrDefault(s.OwnerId, s.OwnerId.ToString()), s.Closed, s.StoreTypeId, s.Origin);
    }

    public async Task<ShopManageListingPage> GetStoreManageListingsAsync(
        ulong guildId, Guid storeId, ShopListingStatus? status, string? search, string sort, bool desc,
        int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.ShopListings.AsNoTracking().Include(l => l.Tags)
            .Where(l => l.GuildId == guildId && l.StoreId == storeId);

        if (status is { } st)
        {
            query = query.Where(l => l.Status == st);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(l => EF.Functions.Like(l.Name, $"%{term}%"));
        }

        query = (sort, desc) switch
        {
            ("price", false) => query.OrderBy(l => l.Price),
            ("price", true) => query.OrderByDescending(l => l.Price),
            ("name", false) => query.OrderBy(l => l.Name),
            ("name", true) => query.OrderByDescending(l => l.Name),
            (_, false) => query.OrderBy(l => l.CreatedAt),
            _ => query.OrderByDescending(l => l.CreatedAt),
        };

        var total = await query.CountAsync(ct);
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new ShopManageListingPage([], total, page, totalPages);
        }

        var codes = await db.Currencies.AsNoTracking().Where(c => c.GuildId == guildId)
            .ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var categories = await db.ShopCategories.AsNoTracking().Where(c => c.GuildId == guildId)
            .ToDictionaryAsync(c => c.Id, c => new { c.Name, c.Icon }, ct);

        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(l =>
        {
            var cat = l.CategoryId is { } c ? categories.GetValueOrDefault(c) : null;
            return new ShopManageListingRow(
                l.Id, l.Name, l.Price, codes.GetValueOrDefault(l.CurrencyId, "?"), l.Status, l.Quantity, l.ThumbKey,
                cat?.Name, l.CreatedAt,
                Moderated: l.DelistedBy is { } d && d != l.SellerId, DelistReason: l.DelistReason,
                CategoryIcon: cat?.Icon, Tags: l.Tags.Select(t => t.Tag).ToList(),
                Featured: l.FeaturedUntil is { } fu && fu > now);
        }).ToList();
        return new ShopManageListingPage(items, total, page, totalPages);
    }

    public async Task<IReadOnlyList<ShopCategory>> GetCategoriesAsync(ulong guildId, CancellationToken ct = default)
        => await db.ShopCategories.AsNoTracking().Where(c => c.GuildId == guildId)
            .OrderBy(c => c.Sort).ThenBy(c => c.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<ShopStoreType>> GetStoreTypesAsync(ulong guildId, CancellationToken ct = default)
        => await db.ShopStoreTypes.AsNoTracking().Where(t => t.GuildId == guildId)
            .OrderBy(t => t.Sort).ThenBy(t => t.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<ShopCurrencyChoice>> GetSpendableCurrenciesAsync(ulong guildId, CancellationToken ct = default)
    {
        // Mirror PostListingAsync's rule: spendable currencies, narrowed to the shop's allow-list when one is set,
        // so the picker can never offer a currency the server would reject.
        var allowed = (await db.GuildShopSettings.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct))?.AllowedCurrencyIds ?? [];
        var query = db.Currencies.AsNoTracking().Where(c => c.GuildId == guildId && c.IsSpendable);
        if (allowed.Count > 0)
        {
            query = query.Where(c => allowed.Contains(c.Id));
        }

        return await query.OrderBy(c => c.Code).Select(c => new ShopCurrencyChoice(c.Id, c.Code, c.Name)).ToListAsync(ct);
    }

    public Task<IReadOnlyList<ShopOrderRow>> GetPurchasesAsync(ulong guildId, ulong buyerId, Guid? storeId = null, CancellationToken ct = default)
        => OrderRowsAsync(guildId, db.ListOrdersByBuyerAsync(guildId, buyerId, ct), storeId, ct);

    public Task<IReadOnlyList<ShopOrderRow>> GetSalesAsync(ulong guildId, ulong sellerId, Guid? storeId = null, CancellationToken ct = default)
        => OrderRowsAsync(guildId, db.ListOrdersBySellerAsync(guildId, sellerId, ct), storeId, ct);

    public Task<IReadOnlyList<ShopOrderRow>> GetDisputesAsync(ulong guildId, Guid? storeId = null, CancellationToken ct = default)
        => OrderRowsAsync(guildId, db.ListDisputedInGuildAsync(guildId, ct), storeId, ct);

    public async Task<ShopOrderDetail?> GetOrderAsync(ulong guildId, Guid orderId, CancellationToken ct = default)
    {
        var o = await db.ShopOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == orderId && x.GuildId == guildId, ct);
        if (o is null)
        {
            return null;
        }

        var code = await CurrencyCodeAsync(guildId, o.CurrencyId, ct);
        var names = await NamesAsync([o.BuyerId, o.SellerId], ct);
        var avatars = await AvatarsAsync([o.BuyerId, o.SellerId], ct);
        var cfg = await db.GuildShopSettings.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct);
        var twoStep = cfg?.TwoStepDelivery ?? false;
        var ratingsEnabled = cfg?.RatingsEnabled ?? true;
        var (sellerAvg, sellerCount) = await db.RatingSummaryAsync(guildId, RatingContext.ShopOrder, o.SellerId, RatingRole.Provider, ct);
        var (buyerAvg, buyerCount) = await db.RatingSummaryAsync(guildId, RatingContext.ShopOrder, o.BuyerId, RatingRole.Consumer, ct);

        // Identity: store (from the order's snapshotted StoreId) + the still-present listing's image/category.
        var storeId = o.StoreId == Guid.Empty ? (Guid?)null : o.StoreId;
        string? storeName = null, storeSlug = null;
        if (storeId is { } sidv)
        {
            var st = await db.ShopStores.AsNoTracking().Where(s => s.Id == sidv).Select(s => new { s.Name, s.Slug }).FirstOrDefaultAsync(ct);
            storeName = st?.Name;
            storeSlug = st?.Slug;
        }

        var li = await db.ShopListings.AsNoTracking().Where(l => l.Id == o.ListingId)
            .Select(l => new { l.ThumbKey, l.CategoryId }).FirstOrDefaultAsync(ct);
        var listingAvailable = li is not null;
        string? catName = null, catIcon = null;
        if (li?.CategoryId is { } cid)
        {
            var c = await db.ShopCategories.AsNoTracking().Where(x => x.Id == cid).Select(x => new { x.Name, x.Icon }).FirstOrDefaultAsync(ct);
            catName = c?.Name;
            catIcon = c?.Icon;
        }

        // When the buyer still has to confirm, surface the auto-settle deadline (clock starts at delivery under
        // two-step delivery, otherwise at purchase).
        DateTimeOffset? autoSettleAt = null;
        if (o.Status is ShopOrderStatus.PendingDelivery or ShopOrderStatus.Delivered && (cfg?.DeliveryConfirmTimeoutHours ?? 0) > 0)
        {
            autoSettleAt = (o.DeliveredAt ?? o.CreatedAt).AddHours(cfg!.DeliveryConfirmTimeoutHours);
        }

        // Offer expiry + dispute timing are derived from StatusChangedAt (when the current state began) + the guild's
        // timeout settings — no extra timestamps needed.
        DateTimeOffset? offerExpiresAt = o.Status == ShopOrderStatus.OfferPending && (cfg?.OfferExpiryHours ?? 0) > 0
            ? o.StatusChangedAt.AddHours(cfg!.OfferExpiryHours) : null;
        DateTimeOffset? disputeRaisedAt = o.Status == ShopOrderStatus.Disputed ? o.StatusChangedAt : null;
        DateTimeOffset? disputeAutoResolveAt = o.Status == ShopOrderStatus.Disputed && (cfg?.DisputeTimeoutHours ?? 0) > 0
            ? o.StatusChangedAt.AddHours(cfg!.DisputeTimeoutHours) : null;

        return new ShopOrderDetail(
            o.Id, o.ListingId, listingAvailable, o.ItemNameSnapshot, o.Quantity, o.Amount, o.FeeAmount, code, o.Status,
            o.BuyerId, names.GetValueOrDefault(o.BuyerId, o.BuyerId.ToString()),
            o.SellerId, names.GetValueOrDefault(o.SellerId, o.SellerId.ToString()),
            o.CreatedAt, o.DeliveredAt, o.ConfirmedAt, o.SettledAt, o.RatingWindowClosesAt, o.DisputedBy, o.DisputeReason, o.DisputeEvidenceKey,
            o.OfferProposedBy, twoStep, ratingsEnabled, o.RatingsClosed,
            SellerRatingAvg: sellerAvg, SellerRatingCount: sellerCount, BuyerRatingAvg: buyerAvg, BuyerRatingCount: buyerCount,
            StoreId: storeId, StoreName: storeName, StoreSlug: storeSlug,
            ThumbKey: li?.ThumbKey, CategoryName: catName, CategoryIcon: catIcon,
            BuyerAvatarUrl: avatars.GetValueOrDefault(o.BuyerId), SellerAvatarUrl: avatars.GetValueOrDefault(o.SellerId),
            AutoSettleAt: autoSettleAt, OfferExpiresAt: offerExpiresAt, DisputeRaisedAt: disputeRaisedAt,
            DisputeAutoResolveAt: disputeAutoResolveAt, MinPrice: cfg?.MinPrice ?? 0, MaxPrice: cfg?.MaxPrice ?? 0);
    }

    public async Task<ShopOrderRatings> GetOrderRatingsAsync(ulong guildId, Guid orderId, ulong viewerId, CancellationToken ct = default)
    {
        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => r.GuildId == guildId && r.Context == RatingContext.ShopOrder && r.SourceId == orderId)
            .ToListAsync(ct);
        if (ratings.Count == 0)
        {
            return new ShopOrderRatings(false, null, null);
        }

        var names = await NamesAsync(ratings.Select(r => r.RaterId).Distinct().ToList(), ct);
        ShopRatingView View(Muster.Domain.Entities.Ratings.Rating r, bool revealComment) => new(
            r.Id, r.RaterId, names.GetValueOrDefault(r.RaterId, r.RaterId.ToString()), r.SubjectId, r.Role, r.Stars,
            revealComment ? r.Comment : null, r.Hidden, r.Moderated, r.CreatedAt);

        var mineRow = ratings.FirstOrDefault(r => r.RaterId == viewerId);
        // The counterparty's rating is whatever the viewer didn't write; visible to them only once revealed
        // (and not moderated out).
        var otherRow = ratings.FirstOrDefault(r => r.RaterId != viewerId);
        var mine = mineRow is null ? null : View(mineRow, revealComment: true);
        var counterparty = otherRow is null || otherRow.Hidden || otherRow.Moderated ? null : View(otherRow, revealComment: true);
        var bothRated = ratings.Count(r => !r.Moderated) >= 2;
        return new ShopOrderRatings(bothRated, mine, counterparty);
    }

    private async Task<IReadOnlyList<ShopOrderRow>> OrderRowsAsync(
        ulong guildId, Task<List<Muster.Domain.Entities.Shops.ShopOrder>> ordersTask, Guid? storeFilter, CancellationToken ct)
    {
        var orders = await ordersTask;
        if (orders.Count == 0)
        {
            return [];
        }

        // StoreId is snapshotted on the order (survives the listing being deleted) — filter + name from it directly.
        if (storeFilter is { } sid)
        {
            orders = orders.Where(o => o.StoreId == sid).ToList();
            if (orders.Count == 0)
            {
                return [];
            }
        }

        var codes = await db.Currencies.AsNoTracking().Where(c => c.GuildId == guildId)
            .ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var userIds = orders.SelectMany(o => new[] { o.BuyerId, o.SellerId }).Distinct().ToList();
        var names = await NamesAsync(userIds, ct);
        var avatars = await AvatarsAsync(userIds, ct);
        var storeIds = orders.Select(o => o.StoreId).Where(s => s != Guid.Empty).Distinct().ToList();
        var storeNames = storeIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.ShopStores.AsNoTracking().Where(s => storeIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        // Item thumbnail + category come from the (possibly still-present) listing; absent if it was deleted.
        var listingIds = orders.Select(o => o.ListingId).Distinct().ToList();
        var listingInfo = await db.ShopListings.AsNoTracking().Where(l => listingIds.Contains(l.Id))
            .Select(l => new { l.Id, l.ThumbKey, l.CategoryId }).ToListAsync(ct);
        var listings = listingInfo.ToDictionary(l => l.Id);
        var catIds = listingInfo.Where(l => l.CategoryId is not null).Select(l => l.CategoryId!.Value).Distinct().ToList();
        var cats = catIds.Count == 0
            ? new Dictionary<Guid, (string Name, string? Icon)>()
            : await db.ShopCategories.AsNoTracking().Where(c => catIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => new ValueTuple<string, string?>(c.Name, c.Icon), ct);

        // Offer-expiry deadline for any OfferPending rows (StatusChangedAt + the guild's offer window).
        var offerHours = orders.Any(o => o.Status == ShopOrderStatus.OfferPending)
            ? (await db.GuildShopSettings.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct))?.OfferExpiryHours ?? 0
            : 0;

        return orders.Select(o =>
        {
            var li = listings.GetValueOrDefault(o.ListingId);
            (string Name, string? Icon)? cat = li?.CategoryId is { } cid ? cats.GetValueOrDefault(cid) : null;
            DateTimeOffset? offerExpires = o.Status == ShopOrderStatus.OfferPending && offerHours > 0
                ? o.StatusChangedAt.AddHours(offerHours) : null;
            return new ShopOrderRow(
                o.Id, o.ListingId, o.ItemNameSnapshot, o.Amount, o.FeeAmount, codes.GetValueOrDefault(o.CurrencyId, "?"),
                o.Quantity, o.Status, o.OfferProposedBy, o.BuyerId, names.GetValueOrDefault(o.BuyerId, o.BuyerId.ToString()),
                o.SellerId, names.GetValueOrDefault(o.SellerId, o.SellerId.ToString()), o.CreatedAt,
                StoreId: o.StoreId == Guid.Empty ? null : o.StoreId,
                StoreName: o.StoreId == Guid.Empty ? null : storeNames.GetValueOrDefault(o.StoreId),
                BuyerAvatarUrl: avatars.GetValueOrDefault(o.BuyerId), SellerAvatarUrl: avatars.GetValueOrDefault(o.SellerId),
                ThumbKey: li?.ThumbKey, CategoryName: cat?.Name, CategoryIcon: cat?.Icon, OfferExpiresAt: offerExpires);
        }).ToList();
    }

    // --- helpers ---

    private async Task<List<ShopListingCard>> ToCardsAsync(ulong guildId, List<ShopListing> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var codes = await db.Currencies.AsNoTracking().Where(c => c.GuildId == guildId)
            .ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var categories = await db.ShopCategories.AsNoTracking().Where(c => c.GuildId == guildId)
            .ToDictionaryAsync(c => c.Id, c => new { c.Name, c.Icon }, ct);
        var sellerIds = rows.Select(r => r.SellerId).Distinct().ToList();
        var names = await NamesAsync(sellerIds, ct);
        var avatars = await AvatarsAsync(sellerIds, ct);

        return rows.Select(l =>
        {
            var cat = l.CategoryId is { } c ? categories.GetValueOrDefault(c) : null;
            return new ShopListingCard(
                l.Id, l.Name, l.Description, l.Price, codes.GetValueOrDefault(l.CurrencyId, "?"), l.ThumbKey,
                l.StoreId, l.Store?.Name ?? string.Empty, l.Store?.Slug ?? string.Empty,
                l.SellerId, names.GetValueOrDefault(l.SellerId, l.SellerId.ToString()),
                l.CategoryId, cat?.Name,
                l.Tags.Select(t => t.Tag).ToList(), l.Status, l.CreatedAt, l.ExpiresAt,
                l.Quantity, Featured: l.FeaturedUntil is { } fu && fu > DateTimeOffset.UtcNow,
                SellerAvatarUrl: avatars.GetValueOrDefault(l.SellerId), CategoryIcon: cat?.Icon);
        }).ToList();
    }

    private async Task<string> CurrencyCodeAsync(ulong guildId, Guid currencyId, CancellationToken ct)
        => (await db.Currencies.AsNoTracking().Where(c => c.GuildId == guildId && c.Id == currencyId)
            .Select(c => c.Code).FirstOrDefaultAsync(ct)) ?? "?";

    private async Task<Dictionary<ulong, string>> NamesAsync(IReadOnlyCollection<ulong> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        return await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.GlobalName ?? u.Username, ct);
    }

    /// <summary>Resolves CDN avatar URLs for a set of users (null when the user has no synced avatar) — feeds the
    /// shared UserChip control wherever the shop shows a seller/owner.</summary>
    private async Task<Dictionary<ulong, string?>> AvatarsAsync(IReadOnlyCollection<ulong> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var rows = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.AvatarHash }).ToListAsync(ct);
        return rows.ToDictionary(u => u.Id, u => Muster.Infrastructure.Discord.DiscordCdn.AvatarUrl(u.Id, u.AvatarHash));
    }
}
