using Muster.Bot.Shop.Rendering;
using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using NetCord.Rest;
using Xunit;

namespace Muster.Bot.UnitTests.Shop.Rendering;

/// <summary>
/// The shop browse-hub builder is pure (no Discord/IO), so we can pin the components + custom_id encoding the
/// ephemeral hub renders. Each test states the user-visible rule it locks.
/// </summary>
public class ShopComponentBuilderTests
{
    private const ulong Guild = 7;
    private static readonly Guid Listing = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CatId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ShopListingCard Card(string name = "Sword", long price = 50) =>
        new(Guid.NewGuid(), name, "", price, "COIN", null, Guid.NewGuid(), "Armory", "armory", 10, "Seller",
            null, null, [], ShopListingStatus.Active, DateTimeOffset.UnixEpoch, null);

    private static ShopBoardPage Page(int count = 3, int page = 1, int totalPages = 1) =>
        new(Enumerable.Range(0, count).Select(i => Card($"Item{i}")).ToList(), count, page, totalPages);

    private static ShopListingDetail Detail(ShopListingStatus status = ShopListingStatus.Active, bool offersOpen = true) =>
        new(Listing, "Sword", "sharp", 50, Guid.NewGuid(), "COIN", null, 1, status, Guid.NewGuid(), "Armory", "armory",
            10, "Seller", null, null, [], DateTimeOffset.UnixEpoch, null, offersOpen);

    private static IReadOnlyList<ShopCategoryOption> Cats() => [new(CatId, "Weapons")];

    private static IEnumerable<ButtonProperties> Buttons(IMessageComponentProperties row) =>
        ((ActionRowProperties)row).Components.OfType<ButtonProperties>();

    // ---- category key round-trip -----------------------------------------------------------------

    [Fact]
    public void Scope_RoundTrips()
    {
        Assert.Equal("all", ShopComponentBuilder.ScopeOf());
        Assert.Equal((null, null), ShopComponentBuilder.ParseScope("all"));
        Assert.Equal((CatId, (Guid?)null), ShopComponentBuilder.ParseScope(ShopComponentBuilder.ScopeOf(categoryId: CatId)));
        Assert.Equal(((Guid?)null, CatId), ShopComponentBuilder.ParseScope(ShopComponentBuilder.ScopeOf(storeId: CatId)));
        Assert.True(ShopComponentBuilder.IsStoreScope(ShopComponentBuilder.ScopeOf(storeId: CatId)));
        Assert.False(ShopComponentBuilder.IsStoreScope("all"));
    }

    // ---- board layout ----------------------------------------------------------------------------

    private const string Sort = ShopComponentBuilder.SortNewest;

    private static StringMenuProperties Menu(IReadOnlyList<IMessageComponentProperties> rows, string prefix) =>
        rows.OfType<StringMenuProperties>().Single(m => m.CustomId.StartsWith(prefix + ":"));

    [Fact]
    public void Board_HasCategoryFilter_Sort_ItemPicker_AndMyOrders()
    {
        var rows = ShopComponentBuilder.Board(Page(), Cats(), ShopComponentBuilder.ScopeAll, Sort, Guild);

        Assert.Equal($"{ShopComponentBuilder.Category}:{Guild}:{Sort}", Menu(rows, ShopComponentBuilder.Category).CustomId);
        Assert.Equal($"{ShopComponentBuilder.Sort}:{Guild}:all", Menu(rows, ShopComponentBuilder.Sort).CustomId);
        Assert.Equal($"{ShopComponentBuilder.Pick}:{Guild}:all:{Sort}", Menu(rows, ShopComponentBuilder.Pick).CustomId);
        Assert.Contains(Buttons(rows[^1]), b => b.CustomId!.StartsWith(ShopComponentBuilder.MyOrders + ":"));
    }

    [Fact]
    public void Board_EmptyPage_OmitsItemPicker()
    {
        var rows = ShopComponentBuilder.Board(Page(count: 0), Cats(), ShopComponentBuilder.ScopeAll, Sort, Guild);
        Assert.DoesNotContain(rows, r => r is StringMenuProperties m && m.CustomId.StartsWith(ShopComponentBuilder.Pick + ":"));
    }

    [Fact]
    public void Board_FirstPage_HasNextNotPrev()
    {
        var rows = ShopComponentBuilder.Board(Page(page: 1, totalPages: 3), Cats(), "all", Sort, Guild);
        var nav = Buttons(rows[^1]).ToList();
        Assert.Contains(nav, b => b.CustomId == $"{ShopComponentBuilder.Nav}:{Guild}:all:{Sort}:2");
        Assert.DoesNotContain(nav, b => b.CustomId!.EndsWith(":0"));
    }

    [Fact]
    public void Board_MiddlePage_HasBothPrevAndNext()
    {
        var rows = ShopComponentBuilder.Board(Page(page: 2, totalPages: 3), Cats(), "all", Sort, Guild);
        var nav = Buttons(rows[^1]).ToList();
        Assert.Contains(nav, b => b.CustomId == $"{ShopComponentBuilder.Nav}:{Guild}:all:{Sort}:1");
        Assert.Contains(nav, b => b.CustomId == $"{ShopComponentBuilder.Nav}:{Guild}:all:{Sort}:3");
    }

    [Fact]
    public void Board_LastPage_HasPrevNotNext()
    {
        var rows = ShopComponentBuilder.Board(Page(page: 3, totalPages: 3), Cats(), "all", Sort, Guild);
        var nav = Buttons(rows[^1]).ToList();
        Assert.Contains(nav, b => b.CustomId == $"{ShopComponentBuilder.Nav}:{Guild}:all:{Sort}:2");
        Assert.DoesNotContain(nav, b => b.CustomId == $"{ShopComponentBuilder.Nav}:{Guild}:all:{Sort}:4");
    }

    [Fact]
    public void ResolveSort_MapsKeysAndDefaults()
    {
        Assert.Equal(("price", false), ShopComponentBuilder.ResolveSort("plo"));
        Assert.Equal(("price", true), ShopComponentBuilder.ResolveSort("phi"));
        Assert.Equal(("created", true), ShopComponentBuilder.ResolveSort("bogus")); // default
    }

    // ---- listing detail buttons ------------------------------------------------------------------

    [Fact]
    public void Listing_Active_WithOffers_HasBuyOfferBack()
    {
        var rows = ShopComponentBuilder.Listing(Detail(offersOpen: true), "all", Sort, Guild);
        var buttons = Buttons(rows[0]).ToList();
        Assert.Contains(buttons, b => b.CustomId == $"{ShopComponentBuilder.Buy}:{Guild}:{Listing}");
        Assert.Contains(buttons, b => b.CustomId == $"{ShopComponentBuilder.Offer}:{Guild}:{Listing}");
        Assert.Contains(buttons, b => b.CustomId!.StartsWith(ShopComponentBuilder.Back + ":"));
    }

    [Fact]
    public void Listing_Active_NoOffers_HasBuyAndBack_NoOffer()
    {
        var buttons = Buttons(ShopComponentBuilder.Listing(Detail(offersOpen: false), "all", Sort, Guild)[0]).ToList();
        Assert.Contains(buttons, b => b.CustomId!.StartsWith(ShopComponentBuilder.Buy + ":"));
        Assert.DoesNotContain(buttons, b => b.CustomId!.StartsWith(ShopComponentBuilder.Offer + ":"));
    }

    [Fact]
    public void Listing_Inactive_OnlyHasBack()
    {
        var buttons = Buttons(ShopComponentBuilder.Listing(Detail(ShopListingStatus.SoldOut), "all", Sort, Guild)[0]).ToList();
        var only = Assert.Single(buttons);
        Assert.StartsWith(ShopComponentBuilder.Back + ":", only.CustomId);
    }

    // ---- store hero card -------------------------------------------------------------------------

    [Fact]
    public void StoreHero_WithFeatured_HasFeaturedButtonsAndBrowse_OneRow()
    {
        var store = Guid.NewGuid();
        var featured = new[] { Card("Hot", 99) };
        var rows = ShopComponentBuilder.StoreHero(Guild, store, featured);

        var row = Assert.Single(rows.OfType<ActionRowProperties>()); // featured + browse on ONE row
        var buttons = row.Components.OfType<ButtonProperties>().ToList();
        Assert.Contains(buttons, b => b.CustomId!.StartsWith(ShopComponentBuilder.Featured + ":"));
        Assert.Contains(buttons, b => b.CustomId == $"{ShopComponentBuilder.Home}:{Guild}:{store:N}");
    }

    [Fact]
    public void StoreHero_NoFeatured_HasBrowseOnly()
    {
        var rows = ShopComponentBuilder.StoreHero(Guild, Guid.NewGuid(), []);
        var only = Assert.Single(rows.OfType<ActionRowProperties>().SelectMany(r => r.Components.OfType<ButtonProperties>()));
        Assert.StartsWith(ShopComponentBuilder.Home + ":", only.CustomId);
    }

    [Fact]
    public void StoreDirectory_ListsStores()
    {
        var menu = ShopComponentBuilder.StoreDirectory(Guild, [new(Guid.NewGuid(), "Armory", 4)]);
        Assert.Equal($"{ShopComponentBuilder.Directory}:{Guild}", menu.CustomId);
    }

    // ---- offer modal -----------------------------------------------------------------------------

    [Fact]
    public void OfferModal_EncodesIds_AndHasAmountInput()
    {
        var modal = ShopComponentBuilder.OfferModalFor(Guild, Listing);
        Assert.Equal($"{ShopComponentBuilder.OfferModal}:{Guild}:{Listing}", modal.CustomId);
        Assert.NotEmpty(modal.Components);
    }
}
