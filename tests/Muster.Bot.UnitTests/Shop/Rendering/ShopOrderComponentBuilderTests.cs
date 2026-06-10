using Muster.Bot.Shop.Rendering;
using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using NetCord.Rest;
using Xunit;

namespace Muster.Bot.UnitTests.Shop.Rendering;

/// <summary>
/// The order action builder is pure, so we pin which actions surface per status + viewer (the bot analogue of
/// OrderReceipt.razor's gating). Each test states the user-visible rule.
/// </summary>
public class ShopOrderComponentBuilderTests
{
    private const ulong Guild = 7, Buyer = 10, Seller = 20, Manager = 30, Outsider = 40;
    private static readonly Guid Order = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static ShopOrderDetail Detail(
        ShopOrderStatus status,
        ShopOfferParty proposedBy = ShopOfferParty.Buyer,
        bool twoStep = false,
        bool ratingsEnabled = true,
        bool ratingsClosed = false,
        DateTimeOffset? windowCloses = null) =>
        new(Order, Guid.NewGuid(), true, "Sword", 1, 50, 0, "COIN", status, Buyer, "B", Seller, "S",
            Now, null, null, null, windowCloses, null, null, null, proposedBy, twoStep, ratingsEnabled, ratingsClosed);

    private static IEnumerable<ButtonProperties> Buttons(IReadOnlyList<IMessageComponentProperties> rows) =>
        rows.OfType<ActionRowProperties>().SelectMany(r => r.Components.OfType<ButtonProperties>());

    private static bool Has(IReadOnlyList<IMessageComponentProperties> rows, string prefix) =>
        Buttons(rows).Any(b => b.CustomId!.StartsWith(prefix + ":"));

    // ---- pending order -----------------------------------------------------

    [Fact]
    public void Pending_Buyer_HasConfirmAndDispute_NotCancel()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.PendingDelivery), Buyer, isManager: false, alreadyRated: false, Now);
        Assert.True(Has(rows, ShopOrderComponentBuilder.Confirm));
        Assert.True(Has(rows, ShopOrderComponentBuilder.Dispute));
        Assert.False(Has(rows, ShopOrderComponentBuilder.Cancel));
        Assert.False(Has(rows, ShopOrderComponentBuilder.Deliver));
    }

    [Fact]
    public void Pending_Seller_TwoStep_HasDeliverCancelDispute()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.PendingDelivery, twoStep: true), Seller, false, false, Now);
        Assert.True(Has(rows, ShopOrderComponentBuilder.Deliver));
        Assert.True(Has(rows, ShopOrderComponentBuilder.Cancel));
        Assert.True(Has(rows, ShopOrderComponentBuilder.Dispute));
        Assert.False(Has(rows, ShopOrderComponentBuilder.Confirm));
    }

    [Fact]
    public void Pending_Seller_OneStep_HasNoDeliver()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.PendingDelivery, twoStep: false), Seller, false, false, Now);
        Assert.False(Has(rows, ShopOrderComponentBuilder.Deliver));
        Assert.True(Has(rows, ShopOrderComponentBuilder.Cancel));
    }

    [Fact]
    public void Pending_Outsider_HasNothing()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.PendingDelivery), Outsider, false, false, Now);
        Assert.Empty(rows);
    }

    // ---- offer negotiation -------------------------------------------------

    [Fact]
    public void Offer_BuyerProposed_SellerCanAcceptCounterDecline()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.OfferPending, ShopOfferParty.Buyer), Seller, false, false, Now);
        Assert.True(Has(rows, ShopOrderComponentBuilder.Accept));
        Assert.True(Has(rows, ShopOrderComponentBuilder.Counter));
        Assert.True(Has(rows, ShopOrderComponentBuilder.Decline));
    }

    [Fact]
    public void Offer_BuyerProposed_BuyerCannotRespond_OnlyDecline()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.OfferPending, ShopOfferParty.Buyer), Buyer, false, false, Now);
        Assert.False(Has(rows, ShopOrderComponentBuilder.Accept));
        Assert.True(Has(rows, ShopOrderComponentBuilder.Decline));
    }

    [Fact]
    public void Offer_SellerCountered_BuyerCanAccept()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.OfferPending, ShopOfferParty.Seller), Buyer, false, false, Now);
        Assert.True(Has(rows, ShopOrderComponentBuilder.Accept));
        Assert.True(Has(rows, ShopOrderComponentBuilder.Counter));
    }

    // ---- dispute -----------------------------------------------------------

    [Fact]
    public void Disputed_Manager_HasArbitrateButtons()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.Disputed), Manager, isManager: true, alreadyRated: false, Now);
        Assert.True(Has(rows, ShopOrderComponentBuilder.ArbPay));
        Assert.True(Has(rows, ShopOrderComponentBuilder.ArbRefund));
    }

    [Fact]
    public void Disputed_NonManager_HasNothing()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.Disputed), Buyer, isManager: false, alreadyRated: false, Now);
        Assert.Empty(rows);
    }

    // ---- rating ------------------------------------------------------------

    [Fact]
    public void Settled_PartyNotRated_WindowOpen_HasRateSelect()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.Settled, windowCloses: Now.AddHours(1)), Buyer, false, alreadyRated: false, Now);
        Assert.Contains(rows, r => r is StringMenuProperties m && m.CustomId.StartsWith(ShopOrderComponentBuilder.RatePick + ":"));
    }

    [Fact]
    public void Settled_AlreadyRated_NoRateSelect()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.Settled), Buyer, false, alreadyRated: true, Now);
        Assert.DoesNotContain(rows, r => r is StringMenuProperties);
    }

    [Fact]
    public void Settled_RatingsClosed_NoRateSelect()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.Settled, ratingsClosed: true), Buyer, false, false, Now);
        Assert.DoesNotContain(rows, r => r is StringMenuProperties);
    }

    [Fact]
    public void Settled_WindowPassed_NoRateSelect()
    {
        var rows = ShopOrderComponentBuilder.Order(Guild, Detail(ShopOrderStatus.Settled, windowCloses: Now.AddHours(-1)), Buyer, false, false, Now);
        Assert.DoesNotContain(rows, r => r is StringMenuProperties);
    }

    // ---- ids ---------------------------------------------------------------

    [Fact]
    public void Id_Encodes_GuildOrderAndExtra()
    {
        Assert.Equal($"sratem:{Guild}:{Order}:5", ShopOrderComponentBuilder.Id(ShopOrderComponentBuilder.RateModal, Guild, Order, "5"));
        Assert.Equal($"sconf:{Guild}:{Order}", ShopOrderComponentBuilder.Id(ShopOrderComponentBuilder.Confirm, Guild, Order));
    }

    [Fact]
    public void ArbitrateButtons_BothTargetOrder()
    {
        var row = ShopOrderComponentBuilder.ArbitrateButtons(Guild, Order);
        Assert.Equal(2, row.Components.OfType<ButtonProperties>().Count());
        Assert.All(row.Components.OfType<ButtonProperties>(), b => Assert.EndsWith($":{Order}", b.CustomId!));
    }
}
