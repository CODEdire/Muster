using Muster.Bot.Shop.Rendering;
using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using Xunit;

namespace Muster.Bot.UnitTests.Shop.Rendering;

/// <summary>The shop embeds are pure renders — assert the catalogue snapshot + listing card content without Discord.</summary>
public class ShopEmbedRendererTests
{
    private static ShopListingCard Card(string name, long price, bool featured = false) =>
        new(Guid.NewGuid(), name, "", price, "COIN", null, Guid.NewGuid(), "Armory", "armory", 10, "Seller",
            null, null, [], ShopListingStatus.Active, DateTimeOffset.UnixEpoch, null, Featured: featured);

    [Fact]
    public void Board_ListsItems_AndPagesInFooter()
    {
        var page = new ShopBoardPage([Card("Sword", 50), Card("Shield", 30)], 2, 1, 1);
        var embed = ShopEmbedRenderer.Board(page, null);

        Assert.Contains("Sword", embed.Description);
        Assert.Contains("Shield", embed.Description);
        Assert.Contains("2 item(s)", embed.Footer!.Text);
        Assert.Contains("page 1/1", embed.Footer!.Text);
    }

    [Fact]
    public void Board_StarsFeaturedItems()
    {
        var page = new ShopBoardPage([Card("Hot", 99, featured: true), Card("Plain", 5)], 2, 1, 1);
        var embed = ShopEmbedRenderer.Board(page, null);
        Assert.Contains("⭐ **Hot**", embed.Description);
        Assert.Contains("• **Plain**", embed.Description);
    }

    [Fact]
    public void StoreHome_ShowsOwnerItemsAndRating()
    {
        var embed = ShopEmbedRenderer.StoreHero("Armory", "Weapons", "⚔️", "fine wares", "Bob", null, 3, 4.5, 2, null, null, "#7c3aed", [], null, DateTimeOffset.UnixEpoch);

        Assert.Null(embed.Title);                                            // dropped — author carries the name
        Assert.Contains("Armory", embed.Author!.Name);                       // author = shop name (linked)
        Assert.Equal("fine wares", embed.Description);
        Assert.Contains(embed.Fields!, f => f.Name == "Type" && f.Value.Contains("Weapons")); // type → field
        Assert.Equal("Bob", embed.Footer!.Text);                            // owner → footer
        Assert.Contains(embed.Fields!, f => f.Name == "Items" && f.Value == "3");
        Assert.Contains(embed.Fields!, f => f.Name == "Rating" && f.Value.Contains("4.5"));
    }

    [Fact]
    public void StoreHero_NoLogo_PrefixesAuthorWithTypeEmoji()
    {
        var embed = ShopEmbedRenderer.StoreHero("Armory", "Weapons", "⚔️", null, "Bob", null, 0, 0, 0, null, null, null, [], null, DateTimeOffset.UnixEpoch);
        Assert.StartsWith("⚔️", embed.Author!.Name); // emoji stands in for the missing logo
    }

    [Fact]
    public void StoreHero_BannerIsTheImage()
    {
        var embed = ShopEmbedRenderer.StoreHero("Armory", null, null, "x", "Bob", null, 0, 0, 0, null, "https://x/b.png", null, [], null, DateTimeOffset.UnixEpoch);
        Assert.Equal("https://x/b.png", embed.Image!.Url);
    }

    [Fact]
    public void StoreHero_EmptyDescription_FallsBack()
    {
        var embed = ShopEmbedRenderer.StoreHero("Armory", null, null, null, "Bob", null, 0, 0, 0, null, null, null, [], null, DateTimeOffset.UnixEpoch);
        Assert.Equal("Browse this shop.", embed.Description);
        Assert.DoesNotContain(embed.Fields!, f => f.Name == "Rating"); // no rating yet
        Assert.DoesNotContain(embed.Fields!, f => f.Name == "Type");
    }

    [Fact]
    public void StoreHero_ListsFeaturedItems()
    {
        var featured = new[] { Card("Hot Sword", 99) };
        var embed = ShopEmbedRenderer.StoreHero("Armory", null, null, "wares", "Bob", null, 1, 0, 0, null, null, null, featured, null, DateTimeOffset.UnixEpoch);
        Assert.Contains(embed.Fields!, f => f.Name == "Featured" && f.Value.Contains("Hot Sword"));
    }

    [Fact]
    public void Board_Empty_SaysNothingForSale()
    {
        var embed = ShopEmbedRenderer.Board(new ShopBoardPage([], 0, 1, 1), null);
        Assert.Contains("Nothing for sale", embed.Description);
    }

    [Fact]
    public void Board_WithCategory_ShowsItInFooter()
    {
        var embed = ShopEmbedRenderer.Board(new ShopBoardPage([], 0, 1, 1), "Weapons");
        Assert.Contains("Weapons", embed.Footer!.Text);
    }

    [Fact]
    public void Listing_ShowsPriceStoreSeller()
    {
        var detail = new ShopListingDetail(
            Guid.NewGuid(), "Sword", "very sharp", 50, Guid.NewGuid(), "COIN", null, 3, ShopListingStatus.Active,
            Guid.NewGuid(), "Armory", "armory", 10, "Seller", null, "Weapons", ["sharp"], DateTimeOffset.UnixEpoch, null, true);

        var embed = ShopEmbedRenderer.Listing(detail, 7);

        Assert.Equal("Sword", embed.Title);
        Assert.Equal("very sharp", embed.Description);
        Assert.Contains(embed.Fields!, f => f.Name == "Price" && f.Value.Contains("50 COIN"));
        Assert.Contains(embed.Fields!, f => f.Name == "Store" && f.Value == "Armory");
        Assert.Contains(embed.Fields!, f => f.Name == "Available" && f.Value == "3");
        Assert.Contains(embed.Fields!, f => f.Name == "Category" && f.Value == "Weapons");
    }
}
