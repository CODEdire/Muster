using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Entities;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Enums;
using Muster.Infrastructure.Messaging;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Ratings;
using Muster.Infrastructure.Services.Shops;
using Muster.IntegrationTests.TestSupport;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// Phase 1 shop lifecycles driven through the CQRS command handlers (the single funnel: load → authorize →
/// service): store + category + listing CRUD, the ShopCreator sell-gate, and the settings guardrails
/// (spendable/allowed currency, price floor/ceiling, per-seller cap, slug uniqueness).
/// </summary>
public class ShopCommandFlowTests
{
    private const ulong Guild = 1, Master = 1, Seller = 10, Buyer = 20, SellerRole = 999;

    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>().UseInMemoryDatabase($"muster-{Guid.NewGuid()}").Options);

    private sealed record Ctx(MusterDbContext Db, IShopAuthorizer Authz, IShopService Shop, IShopReadService Reads, Currency Coin);

    private static async Task<Ctx> SeededAsync(Action<GuildShopSettings>? configure = null, IShopImageService? images = null)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(Guild, "G", null, ownerId: Master, seedDefaults: false); // owner ⇒ Admin; no seed (tests assert a clean taxonomy)

        // Grant a non-owner Seller the ShopCreator tier via a mapped role.
        db.GuildRoleMappings.Add(new GuildRoleMapping { GuildId = Guild, RoleId = SellerRole, Tiers = GuildRoleTier.ShopCreator });
        db.GuildMembers.Add(new GuildMember { GuildId = Guild, UserId = Seller, RoleIds = [SellerRole] });
        db.GuildMembers.Add(new GuildMember { GuildId = Guild, UserId = Buyer, RoleIds = [] });

        var coin = new Currency { Id = Guid.NewGuid(), GuildId = Guild, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var auth = new GuildAuthorizationService(db);
        var defaults = new GuildShopSettings();
        configure?.Invoke(defaults);
        var settings = new GuildShopSettingsService(db, Options.Create(defaults));
        var currency = new CurrencyService(db, new RecordingMessageBus());
        var ratings = new RatingService(db);
        var shop = new ShopService(db, settings, images ?? new NoOpShopImageService(), currency, auth, ratings, new AuditService(db), new RecordingMessageBus());
        // Feature gate with platform-on (empty config ⇒ default on) + allow-all billing, so the authorizer's gate
        // check is a pass-through here; guild-off is still exercised via the service's own NotActive checks.
        var questSettings = new Muster.Infrastructure.Services.Quests.GuildQuestSettingsService(db, Options.Create(new GuildQuestSettings()));
        var featureGate = new FeatureGate(
            new ConfigurationFeatureSource(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            new AllowAllEntitlementSource(),
            new GuildFeatureSource(settings, questSettings));
        return new Ctx(db, new ShopAuthorizer(auth, featureGate), shop, new ShopReadService(db), coin);
    }

    private static Task FundAsync(Ctx c, ulong userId, long amount) =>
        new CurrencyService(c.Db, new RecordingMessageBus())
            .AwardAsync(Guild, userId, c.Coin.Id, amount, CurrencyLedgerSource.Connector, null, "seed");

    private static async Task<long> BalanceAsync(Ctx c, ulong userId) =>
        await c.Db.CurrencyLedgerEntries
            .Where(e => e.UserId == userId && e.CurrencyId == c.Coin.Id && e.SeasonId == null)
            .SumAsync(e => (long?)e.Amount) ?? 0;

    private static Task<Result<Guid>> BuyAsync(Ctx c, Guid listingId, ulong buyer, int qty = 1)
        => PurchaseListingHandler.Handle(new PurchaseListing(Guild, buyer, listingId, qty), c.Db, c.Authz, c.Shop, default);

    private static Task<Result<Guid>> CreateStoreAsync(Ctx c, ulong actor, string name = "Armory", string? slug = null)
        => CreateStoreHandler.Handle(new CreateStore(Guild, actor, name, "wares", slug), c.Authz, c.Shop, default);

    private static Task<Result<Guid>> PostAsync(Ctx c, Guid storeId, ulong actor, long price = 50, string currency = "COIN", int qty = 1)
        => PostListingHandler.Handle(
            new PostListing(Guild, actor, storeId, "Sword", currency, price, "sharp", null, qty), c.Db, c.Authz, c.Shop, default);

    // ---- Stores ------------------------------------------------------------

    [Fact]
    public async Task ShopCreator_CreatesStore_WithUniqueSlug()
    {
        var c = await SeededAsync();
        var a = await CreateStoreAsync(c, Seller, "Bob's Armory");
        var b = await CreateStoreAsync(c, Master, "Bob's Armory"); // same name, different owner

        Assert.True(a.Ok);
        Assert.True(b.Ok);
        var slugs = await c.Db.ShopStores.Select(s => s.Slug).ToListAsync();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());      // slugs are unique within the guild
        Assert.Contains("bobs-armory", slugs);
    }

    [Fact]
    public async Task NonShopCreator_CannotCreateStore()
    {
        var c = await SeededAsync();
        var result = await CreateStoreAsync(c, Buyer);            // Buyer has no ShopCreator role
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task CreateStore_EnforcesPerSellerCap()
    {
        var c = await SeededAsync(s => s.MaxStoresPerSeller = 1);
        Assert.True((await CreateStoreAsync(c, Seller, "One")).Ok);
        var second = await CreateStoreAsync(c, Seller, "Two");
        Assert.False(second.Ok);
        Assert.Equal(nameof(ShopResult.StoreCapReached), second.Status);
    }

    // ---- Listings ----------------------------------------------------------

    [Fact]
    public async Task PostListing_AppearsOnMarket_AndStorefront()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        Assert.True((await PostAsync(c, store, Seller)).Ok);

        var market = await c.Reads.GetMarketAsync(Guild, null, null, null, "created", true, 1, 20);
        Assert.Single(market.Items);
        Assert.Equal("Sword", market.Items[0].Name);

        var slug = await c.Db.ShopStores.Where(s => s.Id == store).Select(s => s.Slug).SingleAsync();
        var front = await c.Reads.GetStorefrontAsync(Guild, slug);
        Assert.NotNull(front);
        Assert.Single(front!.Listings);
    }

    [Fact]
    public async Task PostListing_OnAnotherUsersStore_IsForbidden()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;   // owned by Seller
        var result = await PostAsync(c, store, Buyer);           // Buyer isn't the owner (nor a creator)
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task PostListing_RejectsNonSpendableCurrency()
    {
        var c = await SeededAsync();
        c.Db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = Guild, Code = "GEM", Name = "Gem", IsSpendable = false });
        await c.Db.SaveChangesAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;

        var result = await PostAsync(c, store, Seller, currency: "GEM");
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.NotSpendable), result.Status);
    }

    [Fact]
    public async Task PostListing_EnforcesPriceFloorAndCeiling()
    {
        var c = await SeededAsync(s => { s.MinPrice = 10; s.MaxPrice = 100; });
        var store = (await CreateStoreAsync(c, Seller)).Value;

        Assert.Equal(nameof(ShopResult.BelowFloor), (await PostAsync(c, store, Seller, price: 5)).Status);
        Assert.Equal(nameof(ShopResult.AboveCeiling), (await PostAsync(c, store, Seller, price: 500)).Status);
        Assert.True((await PostAsync(c, store, Seller, price: 50)).Ok);
    }

    [Fact]
    public async Task PostListing_EnforcesActiveListingCap()
    {
        var c = await SeededAsync(s => s.MaxActiveListingsPerSeller = 1);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        Assert.True((await PostAsync(c, store, Seller)).Ok);
        Assert.Equal(nameof(ShopResult.ListingCapReached), (await PostAsync(c, store, Seller)).Status);
    }

    [Fact]
    public async Task EditListing_UpdatesFields()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50)).Value;

        var result = await EditListingHandler.Handle(
            new EditListing(Guild, Seller, listing, Name: "Great Sword", Price: 80), c.Db, c.Authz, c.Shop, default);
        Assert.True(result.Ok);

        var detail = await c.Reads.GetListingDetailAsync(Guild, listing);
        Assert.Equal("Great Sword", detail!.Name);
        Assert.Equal(80, detail.Price);
    }

    // ---- Categories (shop manager) -----------------------------------------

    [Fact]
    public async Task Category_CreateEditDelete_NullsListingCategory()
    {
        var c = await SeededAsync();
        var cat = (await CreateCategoryHandler.Handle(new CreateCategory(Guild, Master, "Weapons"), c.Authz, c.Shop, default)).Value;
        Assert.True((await EditCategoryHandler.Handle(new EditCategory(Guild, Master, cat, "Blades", 5, 100), c.Db, c.Authz, c.Shop, default)).Ok);

        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostListingHandler.Handle(
            new PostListing(Guild, Seller, store, "Sword", "COIN", 50, "x", cat), c.Db, c.Authz, c.Shop, default)).Value;

        Assert.True((await DeleteCategoryHandler.Handle(new DeleteCategory(Guild, Master, cat), c.Db, c.Authz, c.Shop, default)).Ok);

        Assert.Empty(await c.Db.ShopCategories.ToListAsync());
        Assert.Null((await c.Db.ShopListings.SingleAsync(l => l.Id == listing)).CategoryId); // detached, not dangling
    }

    [Fact]
    public async Task Category_ByNonAdmin_IsForbidden()
    {
        var c = await SeededAsync();
        var result = await CreateCategoryHandler.Handle(new CreateCategory(Guild, Seller, "Weapons"), c.Authz, c.Shop, default);
        Assert.False(result.Ok);   // categories are the admin's global taxonomy — a ShopCreator can't change them
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task DeleteStore_RemovesStoreAndItsListings()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        await PostAsync(c, store, Seller);

        Assert.True((await DeleteStoreHandler.Handle(new DeleteStore(Guild, Seller, store), c.Db, c.Authz, c.Shop, default)).Ok);

        Assert.Empty(await c.Db.ShopStores.ToListAsync());
        Assert.Empty(await c.Db.ShopListings.ToListAsync());   // listings removed with the store
    }

    [Fact]
    public async Task DeleteStore_ByNonOwner_IsForbidden()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var result = await DeleteStoreHandler.Handle(new DeleteStore(Guild, Buyer, store), c.Db, c.Authz, c.Shop, default);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task CancelListing_RemovesFromMarket()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller)).Value;

        Assert.True((await CancelListingHandler.Handle(new CancelListing(Guild, Seller, listing), c.Db, c.Authz, c.Shop, default)).Ok);

        var market = await c.Reads.GetMarketAsync(Guild, null, null, null, "created", true, 1, 20);
        Assert.Empty(market.Items);
        Assert.Equal(ShopListingStatus.Cancelled, (await c.Db.ShopListings.SingleAsync()).Status);
    }

    // ---- Buy-now escrow + commission burn ----------------------------------

    [Fact]
    public async Task BuyNow_HoldConfirm_PaysSellerNet_AndBurnsCommission()
    {
        var c = await SeededAsync(s => s.CommissionBps = 1000); // 10%
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 100);

        // Purchase → buyer debited, funds held in escrow.
        var order = (await BuyAsync(c, listing, Buyer)).Value;
        Assert.Equal(0, await BalanceAsync(c, Buyer));
        Assert.Equal(100, await BalanceAsync(c, CurrencyService.EscrowAccountUserId));
        Assert.Equal(ShopListingStatus.SoldOut, (await c.Db.ShopListings.SingleAsync()).Status);

        // Confirm → seller paid net (90), commission (10) burned, escrow emptied.
        Assert.True((await ConfirmReceiptHandler.Handle(new ConfirmReceipt(Guild, Buyer, order), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Equal(90, await BalanceAsync(c, Seller));
        Assert.Equal(10, await BalanceAsync(c, CurrencyService.BurnAccountUserId));
        Assert.Equal(0, await BalanceAsync(c, CurrencyService.EscrowAccountUserId));
        Assert.Equal(ShopOrderStatus.Settled, (await c.Db.ShopOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task BuyNow_SellerCancel_RefundsBuyer_AndReopensListing()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50)).Value;
        await FundAsync(c, Buyer, 50);

        var order = (await BuyAsync(c, listing, Buyer)).Value;
        Assert.True((await SellerCancelOrderHandler.Handle(new SellerCancelOrder(Guild, Seller, order), c.Db, c.Authz, c.Shop, default)).Ok);

        Assert.Equal(50, await BalanceAsync(c, Buyer));                          // made whole
        Assert.Equal(0, await BalanceAsync(c, CurrencyService.EscrowAccountUserId));
        Assert.Equal(0, await BalanceAsync(c, Seller));                          // no fee on a refund
        var l = await c.Db.ShopListings.SingleAsync();
        Assert.Equal(ShopListingStatus.Active, l.Status);                        // stock released
        Assert.Equal(1, l.Quantity);
    }

    [Fact]
    public async Task BuyNow_InsufficientFunds_IsRejected()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 40);

        var result = await BuyAsync(c, listing, Buyer);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.InsufficientFunds), result.Status);
        Assert.Equal(40, await BalanceAsync(c, Buyer));                          // untouched
    }

    [Fact]
    public async Task BuyNow_OwnListing_IsAllowed_CommissionStillBurns()
    {
        var c = await SeededAsync(s => s.CommissionBps = 1000); // 10%
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Seller, 100);

        // A seller may buy their own listing (e.g. clear stock). Net effect: they lose the burned commission.
        var order = await BuyAsync(c, listing, Seller);
        Assert.True(order.Ok);
        Assert.True((await ConfirmReceiptHandler.Handle(new ConfirmReceipt(Guild, Seller, order.Value), c.Db, c.Authz, c.Shop, default)).Ok);

        Assert.Equal(90, await BalanceAsync(c, Seller)); // paid out 90 (100 − 10 fee), having spent 100
        Assert.Equal(10, await BalanceAsync(c, CurrencyService.BurnAccountUserId));
        Assert.Equal(0, await BalanceAsync(c, CurrencyService.EscrowAccountUserId));
    }

    // ---- Stackable stock / two-step delivery / edit-lock --------------------

    [Fact]
    public async Task Stackable_DecrementsPerSale_ThenOutOfStock()
    {
        const ulong Buyer2 = 30, Buyer3 = 40;
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50, qty: 2)).Value;
        await FundAsync(c, Buyer, 50);
        await FundAsync(c, Buyer2, 50);
        await FundAsync(c, Buyer3, 50);

        Assert.True((await BuyAsync(c, listing, Buyer)).Ok);
        Assert.Equal(ShopListingStatus.Active, (await c.Db.ShopListings.SingleAsync()).Status); // 1 left
        Assert.True((await BuyAsync(c, listing, Buyer2)).Ok);
        Assert.Equal(ShopListingStatus.SoldOut, (await c.Db.ShopListings.SingleAsync()).Status); // 0 left

        var third = await BuyAsync(c, listing, Buyer3);
        Assert.False(third.Ok);
        Assert.Equal(nameof(ShopResult.NotActive), third.Status); // sold out → listing not active
    }

    [Fact]
    public async Task TwoStep_AutoSettleWaitsForMarkDelivered()
    {
        var c = await SeededAsync(s => { s.TwoStepDelivery = true; s.CommissionBps = 0; s.DeliveryConfirmTimeoutHours = 72; });
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 60)).Value;
        await FundAsync(c, Buyer, 60);
        var order = (await BuyAsync(c, listing, Buyer)).Value;

        // Not delivered → never auto-settles, even past the window.
        Assert.Equal(0, await c.Shop.AutoSettleDueAsync(DateTimeOffset.UtcNow.AddHours(99), default));
        Assert.Equal(0, await BalanceAsync(c, Seller));

        // Seller marks delivered → the confirm clock starts; now it auto-settles past the window.
        Assert.True((await MarkDeliveredHandler.Handle(new MarkDelivered(Guild, Seller, order), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Equal(ShopOrderStatus.Delivered, (await c.Db.ShopOrders.SingleAsync()).Status);
        Assert.Equal(1, await c.Shop.AutoSettleDueAsync(DateTimeOffset.UtcNow.AddHours(73), default));
        Assert.Equal(60, await BalanceAsync(c, Seller));
    }

    [Fact]
    public async Task MarkDelivered_ByBuyer_IsForbidden()
    {
        var c = await SeededAsync(s => s.TwoStepDelivery = true);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50)).Value;
        await FundAsync(c, Buyer, 50);
        var order = (await BuyAsync(c, listing, Buyer)).Value;

        var result = await MarkDeliveredHandler.Handle(new MarkDelivered(Guild, Buyer, order), c.Db, c.Authz, c.Shop, default);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task EditListing_LockedWhileItHasLiveOrders()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50, qty: 2)).Value; // qty 2 so it stays Active after one buy
        await FundAsync(c, Buyer, 50);
        await BuyAsync(c, listing, Buyer);

        var result = await EditListingHandler.Handle(
            new EditListing(Guild, Seller, listing, Price: 80), c.Db, c.Authz, c.Shop, default);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.HasOrders), result.Status);
    }

    // ---- Offers (binding hold, seller approves) -----------------------------

    private static Task<Result<Guid>> OfferAsync(Ctx c, Guid listingId, ulong buyer, long amount, int qty = 1)
        => MakeOfferHandler.Handle(new MakeOffer(Guild, buyer, listingId, amount, qty), c.Db, c.Authz, c.Shop, default);

    [Fact]
    public async Task Offer_HoldAcceptConfirm_SettlesAtOfferedPrice()
    {
        var c = await SeededAsync(s => s.CommissionBps = 0);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 80);

        // Offer 80 → held in escrow, order OfferPending, stock untouched.
        var order = (await OfferAsync(c, listing, Buyer, 80)).Value;
        Assert.Equal(0, await BalanceAsync(c, Buyer));
        Assert.Equal(80, await BalanceAsync(c, CurrencyService.EscrowAccountUserId));
        Assert.Equal(ShopOrderStatus.OfferPending, (await c.Db.ShopOrders.SingleAsync()).Status);
        Assert.Equal(ShopListingStatus.Active, (await c.Db.ShopListings.SingleAsync()).Status);

        // Seller accepts → live order; buyer confirms → seller paid the offered 80.
        Assert.True((await AcceptOfferHandler.Handle(new AcceptOffer(Guild, Seller, order), c.Db, c.Shop, default)).Ok);
        Assert.Equal(ShopListingStatus.SoldOut, (await c.Db.ShopListings.SingleAsync()).Status);
        Assert.True((await ConfirmReceiptHandler.Handle(new ConfirmReceipt(Guild, Buyer, order), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Equal(80, await BalanceAsync(c, Seller));
    }

    [Fact]
    public async Task Offer_Accept_DeclinesAndRefundsCompetingOffers()
    {
        const ulong Buyer2 = 30;
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 70);
        await FundAsync(c, Buyer2, 90);

        var offer1 = (await OfferAsync(c, listing, Buyer, 70)).Value;
        await OfferAsync(c, listing, Buyer2, 90);

        // Accept Buyer's offer → single-unit listing sells out → Buyer2's offer is declined + refunded.
        Assert.True((await AcceptOfferHandler.Handle(new AcceptOffer(Guild, Seller, offer1), c.Db, c.Shop, default)).Ok);
        Assert.Equal(90, await BalanceAsync(c, Buyer2));                      // refunded
        Assert.Equal(70, await BalanceAsync(c, CurrencyService.EscrowAccountUserId)); // only the accepted hold remains
        var statuses = await c.Db.ShopOrders.Select(o => o.Status).ToListAsync();
        Assert.Contains(ShopOrderStatus.PendingDelivery, statuses);
        Assert.Contains(ShopOrderStatus.OfferDeclined, statuses);
    }

    [Fact]
    public async Task Offer_Decline_RefundsBuyer()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 60);
        var order = (await OfferAsync(c, listing, Buyer, 60)).Value;

        Assert.True((await DeclineOfferHandler.Handle(new DeclineOffer(Guild, Seller, order), c.Db, c.Shop, default)).Ok);
        Assert.Equal(60, await BalanceAsync(c, Buyer));
        Assert.Equal(ShopOrderStatus.OfferDeclined, (await c.Db.ShopOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Offer_WithdrawByBuyer_Refunds()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 60);
        var order = (await OfferAsync(c, listing, Buyer, 60)).Value;

        // A non-party can't end the offer; the buyer withdrawing refunds the held funds.
        Assert.False((await WithdrawOfferHandler.Handle(new WithdrawOffer(Guild, 999, order), c.Db, c.Shop, default)).Ok);
        Assert.True((await WithdrawOfferHandler.Handle(new WithdrawOffer(Guild, Buyer, order), c.Db, c.Shop, default)).Ok);
        Assert.Equal(60, await BalanceAsync(c, Buyer));
    }

    [Fact]
    public async Task Counter_BouncesBackAndForth_HoldsFollowTheBuyerProposedPrice()
    {
        var c = await SeededAsync(s => s.CommissionBps = 0);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 100);

        // Buyer offers 60 → held.
        var order = (await OfferAsync(c, listing, Buyer, 60)).Value;
        Assert.Equal(60, await BalanceAsync(c, CurrencyService.EscrowAccountUserId));
        Assert.Equal(40, await BalanceAsync(c, Buyer));

        // Seller counters 90 → buyer's 60 hold released (seller-proposed prices aren't held).
        Assert.True((await CounterOfferHandler.Handle(new CounterOffer(Guild, Seller, order, 90), c.Db, c.Shop, default)).Ok);
        Assert.Equal(0, await BalanceAsync(c, CurrencyService.EscrowAccountUserId));
        Assert.Equal(100, await BalanceAsync(c, Buyer));

        // Buyer counters back 75 → re-held at the new price.
        Assert.True((await CounterOfferHandler.Handle(new CounterOffer(Guild, Buyer, order, 75), c.Db, c.Shop, default)).Ok);
        Assert.Equal(75, await BalanceAsync(c, CurrencyService.EscrowAccountUserId));
        Assert.Equal(25, await BalanceAsync(c, Buyer));

        // Seller accepts 75 → live order; buyer confirms → seller paid 75.
        Assert.True((await AcceptOfferHandler.Handle(new AcceptOffer(Guild, Seller, order), c.Db, c.Shop, default)).Ok);
        Assert.True((await ConfirmReceiptHandler.Handle(new ConfirmReceipt(Guild, Buyer, order), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Equal(75, await BalanceAsync(c, Seller));
    }

    [Fact]
    public async Task Counter_OnlyTheAwaitedParty_MayActOnTheirTurn()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 100);
        var order = (await OfferAsync(c, listing, Buyer, 60)).Value;   // buyer-proposed → seller's turn

        // It's the seller's turn — the buyer can't counter or accept their own standing offer.
        Assert.Equal(nameof(ShopResult.Forbidden), (await CounterOfferHandler.Handle(new CounterOffer(Guild, Buyer, order, 70), c.Db, c.Shop, default)).Status);
        Assert.Equal(nameof(ShopResult.Forbidden), (await AcceptOfferHandler.Handle(new AcceptOffer(Guild, Buyer, order), c.Db, c.Shop, default)).Status);
    }

    [Fact]
    public async Task Offer_RejectedWhenDisabled_GuildOrListing()
    {
        var c = await SeededAsync(s => s.OffersEnabled = false);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 100);
        Assert.Equal(nameof(ShopResult.NotActive), (await OfferAsync(c, listing, Buyer, 80)).Status); // guild off

        var c2 = await SeededAsync(); // offers on globally, but off for this listing
        var store2 = (await CreateStoreAsync(c2, Seller)).Value;
        var noOffer = (await PostListingHandler.Handle(
            new PostListing(Guild, Seller, store2, "Sword", "COIN", 100, AcceptsOffers: false), c2.Db, c2.Authz, c2.Shop, default)).Value;
        await FundAsync(c2, Buyer, 100);
        Assert.Equal(nameof(ShopResult.NotActive), (await OfferAsync(c2, noOffer, Buyer, 80)).Status); // listing off
    }

    [Fact]
    public async Task AutoExpireOffers_RefundsPastTheWindow()
    {
        var c = await SeededAsync(s => s.OfferExpiryHours = 48);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 60);
        await OfferAsync(c, listing, Buyer, 60);

        Assert.Equal(0, await c.Shop.AutoExpireOffersAsync(DateTimeOffset.UtcNow, default));
        Assert.Equal(1, await c.Shop.AutoExpireOffersAsync(DateTimeOffset.UtcNow.AddHours(49), default));
        Assert.Equal(60, await BalanceAsync(c, Buyer));
        Assert.Equal(ShopOrderStatus.OfferDeclined, (await c.Db.ShopOrders.SingleAsync()).Status);
    }

    // ---- Disputes + arbitration --------------------------------------------

    [Fact]
    public async Task Dispute_ThenArbitratePay_PaysSellerNet_AndBurns()
    {
        var c = await SeededAsync(s => s.CommissionBps = 1000); // 10%
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 100)).Value;
        await FundAsync(c, Buyer, 100);
        var order = (await BuyAsync(c, listing, Buyer)).Value;

        Assert.True((await DisputeOrderHandler.Handle(new DisputeOrder(Guild, Buyer, order, "not as described"), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Equal(ShopOrderStatus.Disputed, (await c.Db.ShopOrders.SingleAsync()).Status);

        // Master is the guild owner ⇒ admin ⇒ shop manager → may arbitrate. Pay seller.
        Assert.True((await ArbitrateOrderHandler.Handle(new ArbitrateOrder(Guild, Master, order, PaySeller: true), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Equal(90, await BalanceAsync(c, Seller));
        Assert.Equal(10, await BalanceAsync(c, CurrencyService.BurnAccountUserId));
        Assert.Equal(0, await BalanceAsync(c, CurrencyService.EscrowAccountUserId));
        Assert.Equal(ShopOrderStatus.Settled, (await c.Db.ShopOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Dispute_ThenArbitrateRefund_RefundsBuyer()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 80)).Value;
        await FundAsync(c, Buyer, 80);
        var order = (await BuyAsync(c, listing, Buyer)).Value;
        await DisputeOrderHandler.Handle(new DisputeOrder(Guild, Seller, order, "buyer never paid out of game"), c.Db, c.Authz, c.Shop, default);

        Assert.True((await ArbitrateOrderHandler.Handle(new ArbitrateOrder(Guild, Master, order, PaySeller: false), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Equal(80, await BalanceAsync(c, Buyer));
        Assert.Equal(0, await BalanceAsync(c, Seller));
        Assert.Equal(ShopOrderStatus.Refunded, (await c.Db.ShopOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Arbitrate_ByNonManager_IsForbidden()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50)).Value;
        await FundAsync(c, Buyer, 50);
        var order = (await BuyAsync(c, listing, Buyer)).Value;
        await DisputeOrderHandler.Handle(new DisputeOrder(Guild, Buyer, order, "x"), c.Db, c.Authz, c.Shop, default);

        var result = await ArbitrateOrderHandler.Handle(new ArbitrateOrder(Guild, Seller, order, PaySeller: true), c.Db, c.Authz, c.Shop, default);
        Assert.False(result.Ok);   // the seller isn't a shop manager
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task AutoResolveDisputes_FavoursTheNonDisputingParty()
    {
        var c = await SeededAsync(s => { s.CommissionBps = 0; s.DisputeTimeoutHours = 72; });
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 60)).Value;
        await FundAsync(c, Buyer, 60);
        var order = (await BuyAsync(c, listing, Buyer)).Value;
        // Buyer raises the dispute → if it lapses, the buyer (disputant) loses → seller is paid.
        await DisputeOrderHandler.Handle(new DisputeOrder(Guild, Buyer, order, "stalling"), c.Db, c.Authz, c.Shop, default);

        Assert.Equal(0, await c.Shop.AutoResolveDisputesAsync(DateTimeOffset.UtcNow, default));      // not yet due
        Assert.Equal(1, await c.Shop.AutoResolveDisputesAsync(DateTimeOffset.UtcNow.AddHours(73), default));

        Assert.Equal(60, await BalanceAsync(c, Seller));
        Assert.Equal(ShopOrderStatus.Settled, (await c.Db.ShopOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task AutoSettle_SettlesOrdersPastTheConfirmWindow()
    {
        var c = await SeededAsync(s => { s.CommissionBps = 0; s.DeliveryConfirmTimeoutHours = 72; });
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 60)).Value;
        await FundAsync(c, Buyer, 60);
        var order = (await BuyAsync(c, listing, Buyer)).Value;

        // Not yet due.
        Assert.Equal(0, await c.Shop.AutoSettleDueAsync(DateTimeOffset.UtcNow, default));
        Assert.Equal(0, await BalanceAsync(c, Seller));

        // Past the 72h window → auto-settles to the seller.
        Assert.Equal(1, await c.Shop.AutoSettleDueAsync(DateTimeOffset.UtcNow.AddHours(73), default));
        Assert.Equal(60, await BalanceAsync(c, Seller));
        Assert.Equal(ShopOrderStatus.Settled, (await c.Db.ShopOrders.SingleAsync()).Status);
    }

    // ---- Ratings (blind-mutual) --------------------------------------------

    /// <summary>A settled buy-now order ready to rate (buyer confirmed receipt).</summary>
    private static async Task<Guid> SettledOrderAsync(Ctx c, long price = 50)
    {
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: price)).Value;
        await FundAsync(c, Buyer, price);
        var order = (await BuyAsync(c, listing, Buyer)).Value;
        Assert.True((await ConfirmReceiptHandler.Handle(new ConfirmReceipt(Guild, Buyer, order), c.Db, c.Authz, c.Shop, default)).Ok);
        return order;
    }

    private static Task<Result> RateAsync(Ctx c, Guid order, ulong actor, int stars, string? comment = null)
        => RateOrderHandler.Handle(new RateOrder(Guild, actor, order, stars, comment), c.Db, c.Authz, c.Shop, default);

    [Fact]
    public async Task Rating_IsBlind_UntilBothSubmit_ThenRevealed()
    {
        var c = await SeededAsync();
        var order = await SettledOrderAsync(c);

        // Buyer rates the seller → held blind; the seller can't see it yet.
        Assert.True((await RateAsync(c, order, Buyer, 5, "fast")).Ok);
        Assert.True((await c.Db.Ratings.SingleAsync(r => r.RaterId == Buyer)).Hidden);
        var sellerView = await c.Reads.GetOrderRatingsAsync(Guild, order, Seller);
        Assert.Null(sellerView.Counterparty);                          // buyer's rating still blind
        Assert.False(sellerView.BothRated);
        // Not yet revealed → not in the seller's reputation.
        Assert.Equal(0, (await c.Db.RatingSummaryAsync(Guild, RatingContext.ShopOrder, Seller, RatingRole.Provider)).Count);

        // Seller rates the buyer → both submitted → mutual reveal.
        Assert.True((await RateAsync(c, order, Seller, 4)).Ok);
        Assert.All(await c.Db.Ratings.ToListAsync(), r => Assert.False(r.Hidden));
        var sellerView2 = await c.Reads.GetOrderRatingsAsync(Guild, order, Seller);
        Assert.NotNull(sellerView2.Counterparty);                      // buyer's 5★ now visible
        Assert.Equal(5, sellerView2.Counterparty!.Stars);
        Assert.True(sellerView2.BothRated);

        var sellerRep = await c.Db.RatingSummaryAsync(Guild, RatingContext.ShopOrder, Seller, RatingRole.Provider);
        Assert.Equal((5d, 1), (sellerRep.Avg, sellerRep.Count));
        var buyerRep = await c.Db.RatingSummaryAsync(Guild, RatingContext.ShopOrder, Buyer, RatingRole.Consumer);
        Assert.Equal((4d, 1), (buyerRep.Avg, buyerRep.Count));
        Assert.True((await c.Db.ShopOrders.SingleAsync()).RatingsClosed);
    }

    [Fact]
    public async Task Rating_Reveals_WhenWindowCloses()
    {
        var c = await SeededAsync(s => s.RatingWindowHours = 168);
        var order = await SettledOrderAsync(c);
        Assert.True((await RateAsync(c, order, Buyer, 3)).Ok);
        Assert.True((await c.Db.Ratings.SingleAsync()).Hidden);

        // Not yet due, then past the window → the one-sided rating reveals.
        Assert.Equal(0, await c.Shop.RevealClosedRatingWindowsAsync(DateTimeOffset.UtcNow, default));
        Assert.Equal(1, await c.Shop.RevealClosedRatingWindowsAsync(DateTimeOffset.UtcNow.AddHours(169), default));

        Assert.False((await c.Db.Ratings.SingleAsync()).Hidden);
        Assert.True((await c.Db.ShopOrders.SingleAsync()).RatingsClosed);
        Assert.Equal(1, (await c.Db.RatingSummaryAsync(Guild, RatingContext.ShopOrder, Seller, RatingRole.Provider)).Count);
    }

    [Fact]
    public async Task Rating_AfterWindowClosed_IsRejected()
    {
        var c = await SeededAsync();
        var order = await SettledOrderAsync(c);
        Assert.Equal(1, await c.Shop.RevealClosedRatingWindowsAsync(DateTimeOffset.UtcNow.AddHours(169), default));

        var late = await RateAsync(c, order, Buyer, 5);
        Assert.False(late.Ok);
        Assert.Equal(nameof(ShopResult.RatingWindowClosed), late.Status);
    }

    [Fact]
    public async Task Rating_Duplicate_IsRejected()
    {
        var c = await SeededAsync();
        var order = await SettledOrderAsync(c);
        Assert.True((await RateAsync(c, order, Buyer, 5)).Ok);

        var again = await RateAsync(c, order, Buyer, 1);
        Assert.False(again.Ok);
        Assert.Equal(nameof(ShopResult.AlreadyRated), again.Status);
    }

    [Fact]
    public async Task Rating_ByNonParty_IsForbidden()
    {
        const ulong Outsider = 50;
        var c = await SeededAsync();
        var order = await SettledOrderAsync(c);

        var result = await RateAsync(c, order, Outsider, 5);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task Rating_OnUnsettledOrder_IsRejected()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50)).Value;
        await FundAsync(c, Buyer, 50);
        var order = (await BuyAsync(c, listing, Buyer)).Value;        // PendingDelivery, not settled

        var result = await RateAsync(c, order, Buyer, 5);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.InvalidState), result.Status);
    }

    [Fact]
    public async Task Rating_Moderated_DropsFromAggregate()
    {
        var c = await SeededAsync();
        var order = await SettledOrderAsync(c);
        Assert.True((await RateAsync(c, order, Buyer, 5)).Ok);
        Assert.True((await RateAsync(c, order, Seller, 5)).Ok);       // both → revealed
        Assert.Equal(1, (await c.Db.RatingSummaryAsync(Guild, RatingContext.ShopOrder, Seller, RatingRole.Provider)).Count);

        // A manager (the owner/admin Master) hides the buyer's rating of the seller.
        var ratingId = await c.Db.Ratings.Where(r => r.SubjectId == Seller).Select(r => r.Id).SingleAsync();
        Assert.True((await ModerateRatingHandler.Handle(new ModerateRating(Guild, Master, ratingId, true), c.Db, c.Authz, c.Shop, default)).Ok);

        Assert.Equal(0, (await c.Db.RatingSummaryAsync(Guild, RatingContext.ShopOrder, Seller, RatingRole.Provider)).Count);
    }

    [Fact]
    public async Task Rating_DisabledForGuild_IsRejected()
    {
        var c = await SeededAsync(s => s.RatingsEnabled = false);
        var order = await SettledOrderAsync(c);

        var result = await RateAsync(c, order, Buyer, 5);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.NotActive), result.Status);
    }

    [Fact]
    public async Task Rating_InvalidStars_IsRejected()
    {
        var c = await SeededAsync();
        var order = await SettledOrderAsync(c);

        var result = await RateAsync(c, order, Buyer, 6);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.InvalidState), result.Status);
    }

    // ---- Featured listings -------------------------------------------------

    private static Task<Result> FeatureAsync(Ctx c, Guid listingId, ulong actor)
        => FeatureListingHandler.Handle(new FeatureListing(Guild, actor, listingId), c.Db, c.Authz, c.Shop, default);

    [Fact]
    public async Task Feature_BurnsFee_AndPromotes()
    {
        var c = await SeededAsync(s => { s.FeaturedListingFee = 10; s.FeaturedDurationHours = 72; });
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50)).Value;
        await FundAsync(c, Seller, 10);

        Assert.True((await FeatureAsync(c, listing, Seller)).Ok);
        Assert.Equal(0, await BalanceAsync(c, Seller));                          // fee debited
        Assert.Equal(10, await BalanceAsync(c, CurrencyService.BurnAccountUserId)); // burned
        Assert.NotNull((await c.Db.ShopListings.SingleAsync(l => l.Id == listing)).FeaturedUntil);
    }

    [Fact]
    public async Task Feature_FreeWhenFeeZero()
    {
        var c = await SeededAsync(s => s.FeaturedListingFee = 0);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller)).Value;

        Assert.True((await FeatureAsync(c, listing, Seller)).Ok);                 // no funds needed
    }

    [Fact]
    public async Task Feature_InsufficientFunds_IsRejected()
    {
        var c = await SeededAsync(s => s.FeaturedListingFee = 100);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller)).Value;
        await FundAsync(c, Seller, 40);

        var result = await FeatureAsync(c, listing, Seller);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.InsufficientFunds), result.Status);
        Assert.Null((await c.Db.ShopListings.SingleAsync()).FeaturedUntil);
    }

    [Fact]
    public async Task Feature_RespectsPerStoreCap()
    {
        var c = await SeededAsync(s => { s.MaxFeaturedPerStore = 1; s.FeaturedListingFee = 0; });
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var a = (await PostAsync(c, store, Seller, qty: 1)).Value;
        var b = (await PostAsync(c, store, Seller, qty: 1)).Value;

        Assert.True((await FeatureAsync(c, a, Seller)).Ok);
        var second = await FeatureAsync(c, b, Seller);
        Assert.False(second.Ok);
        Assert.Equal(nameof(ShopResult.FeaturedCapReached), second.Status);
    }

    [Fact]
    public async Task Feature_NonSeller_IsForbidden()
    {
        var c = await SeededAsync(s => s.FeaturedListingFee = 0);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller)).Value;

        var result = await FeatureAsync(c, listing, Buyer);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task Unfeature_ClearsFeatured_NoRefund()
    {
        var c = await SeededAsync(s => s.FeaturedListingFee = 10);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50)).Value;
        await FundAsync(c, Seller, 10);
        Assert.True((await FeatureAsync(c, listing, Seller)).Ok);
        Assert.Equal(0, await BalanceAsync(c, Seller)); // fee burned

        Assert.True((await UnfeatureListingHandler.Handle(new UnfeatureListing(Guild, Seller, listing), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Null((await c.Db.ShopListings.SingleAsync()).FeaturedUntil); // unfeatured
        Assert.Equal(0, await BalanceAsync(c, Seller));                     // fee NOT refunded

        // Not featured anymore → unfeature again is a no-op error.
        Assert.False((await UnfeatureListingHandler.Handle(new UnfeatureListing(Guild, Seller, listing), c.Db, c.Authz, c.Shop, default)).Ok);
    }

    [Fact]
    public async Task Feature_ExpireSweep_Unfeatures()
    {
        var c = await SeededAsync(s => { s.FeaturedListingFee = 0; s.FeaturedDurationHours = 72; });
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller)).Value;
        Assert.True((await FeatureAsync(c, listing, Seller)).Ok);

        Assert.Equal(0, await c.Shop.ExpireFeaturedDueAsync(DateTimeOffset.UtcNow));        // not yet due
        Assert.Equal(1, await c.Shop.ExpireFeaturedDueAsync(DateTimeOffset.UtcNow.AddHours(73)));
        Assert.Null((await c.Db.ShopListings.SingleAsync()).FeaturedUntil);
    }

    [Fact]
    public async Task Feature_SurvivesSelloutButFreesTheCap()
    {
        var c = await SeededAsync(s => s.FeaturedListingFee = 0);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50, qty: 1)).Value;
        await FundAsync(c, Buyer, 50);
        Assert.True((await FeatureAsync(c, listing, Seller)).Ok);

        Assert.True((await BuyAsync(c, listing, Buyer)).Ok);                       // sells out
        var l = await c.Db.ShopListings.SingleAsync();
        Assert.Equal(ShopListingStatus.SoldOut, l.Status);
        Assert.NotNull(l.FeaturedUntil);                                          // window kept so a relist can carry it
        Assert.Equal(0, await c.Db.CountFeaturedInStoreAsync(store, DateTimeOffset.UtcNow)); // hidden ⇒ frees the slot
    }

    [Fact]
    public async Task Relist_SoldOut_MakesFreshCopy_CarriesFeatured_LinksBack()
    {
        var c = await SeededAsync(s => s.FeaturedListingFee = 0);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50, qty: 1)).Value;
        await FundAsync(c, Buyer, 50);
        Assert.True((await FeatureAsync(c, listing, Seller)).Ok);
        Assert.True((await BuyAsync(c, listing, Buyer)).Ok);                       // sells out (keeps FeaturedUntil)

        var relist = await RelistListingHandler.Handle(
            new RelistListing(Guild, Seller, listing, Quantity: 5), c.Db, c.Authz, c.Shop, default);
        Assert.True(relist.Ok);

        var fresh = await c.Db.ShopListings.SingleAsync(l => l.Id == relist.Value);
        var old = await c.Db.ShopListings.SingleAsync(l => l.Id == listing);
        Assert.Equal(ShopListingStatus.Active, fresh.Status);
        Assert.Equal(5, fresh.Quantity);
        Assert.Equal(listing, fresh.RelistedFromId);                              // links back to history
        Assert.NotNull(fresh.FeaturedUntil);                                      // featured window moved over…
        Assert.Null(old.FeaturedUntil);                                           // …and off the old copy
        Assert.Equal(ShopListingStatus.SoldOut, old.Status);                      // old stays as history
    }

    [Fact]
    public async Task Relist_RejectsActiveListing()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, qty: 3)).Value;

        var relist = await RelistListingHandler.Handle(
            new RelistListing(Guild, Seller, listing, Quantity: 5), c.Db, c.Authz, c.Shop, default);
        Assert.False(relist.Ok);
        Assert.Equal(nameof(ShopResult.InvalidState), relist.Status);
    }

    [Fact]
    public async Task AddStock_IncrementsActiveListing()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, qty: 2)).Value;

        var add = await AddListingStockHandler.Handle(
            new AddListingStock(Guild, Seller, listing, AddUnits: 8), c.Db, c.Authz, c.Shop, default);
        Assert.True(add.Ok);
        Assert.Equal(10, (await c.Db.ShopListings.SingleAsync(l => l.Id == listing)).Quantity);
    }

    [Fact]
    public async Task AddStock_RejectsSoldOut()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50, qty: 1)).Value;
        await FundAsync(c, Buyer, 50);
        Assert.True((await BuyAsync(c, listing, Buyer)).Ok);                       // sells out

        var add = await AddListingStockHandler.Handle(
            new AddListingStock(Guild, Seller, listing, AddUnits: 5), c.Db, c.Authz, c.Shop, default);
        Assert.False(add.Ok);
        Assert.Equal(nameof(ShopResult.NotActive), add.Status);
    }

    [Fact]
    public async Task ResyncChannel_CoversEveryOpenStore()
    {
        var c = await SeededAsync();
        await CreateStoreAsync(c, Seller, "One");
        await CreateStoreAsync(c, Master, "Two");

        Assert.Equal(2, await c.Shop.ResyncShopChannelAsync(Guild)); // a home card per open store
    }

    [Fact]
    public async Task ResyncStore_OkForExisting_NotFoundOtherwise()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;

        Assert.Equal(ShopResult.Ok, await c.Shop.ResyncStoreAsync(Guild, store));
        Assert.Equal(ShopResult.NotFound, await c.Shop.ResyncStoreAsync(Guild, Guid.NewGuid()));
    }

    [Fact]
    public async Task Resync_NonCreator_IsForbidden()
    {
        var c = await SeededAsync();
        var result = await ResyncShopChannelHandler.Handle(new ResyncShopChannel(Guild, Buyer), c.Authz, c.Shop, default);
        Assert.False(result.Ok);
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task ResyncStore_ByOwner_IsAllowed_ByOutsider_Forbidden()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;

        Assert.True((await ResyncShopStoreHandler.Handle(new ResyncShopStore(Guild, Seller, store), c.Db, c.Authz, c.Shop, default)).Ok);

        var forbidden = await ResyncShopStoreHandler.Handle(new ResyncShopStore(Guild, Buyer, store), c.Db, c.Authz, c.Shop, default);
        Assert.False(forbidden.Ok);
        Assert.Equal(nameof(ShopResult.Forbidden), forbidden.Status);
    }

    // ---- Listing expiry + store close --------------------------------------

    [Fact]
    public async Task Listing_ExpiresViaSweep_AndLeavesTheMarket()
    {
        var c = await SeededAsync(); // ListingDefaultExpiryDays = 30 → ExpiresAt ≈ now + 30d
        var store = (await CreateStoreAsync(c, Seller)).Value;
        await PostAsync(c, store, Seller);

        Assert.Equal(0, await c.Shop.ExpireListingsDueAsync(DateTimeOffset.UtcNow));            // not yet due
        Assert.Equal(1, await c.Shop.ExpireListingsDueAsync(DateTimeOffset.UtcNow.AddDays(31)));
        Assert.Equal(ShopListingStatus.Expired, (await c.Db.ShopListings.SingleAsync()).Status);
        Assert.Empty((await c.Reads.GetMarketAsync(Guild, null, null, null, "created", true, 1, 20)).Items);
    }

    [Fact]
    public async Task ClosedStore_BlocksPurchaseAndNewListings()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller, price: 50)).Value;
        await FundAsync(c, Buyer, 50);

        // Close the store.
        Assert.True((await EditStoreHandler.Handle(
            new EditStore(Guild, Seller, store, Closed: true), c.Db, c.Authz, c.Shop, default)).Ok);

        var buy = await BuyAsync(c, listing, Buyer);
        Assert.False(buy.Ok);
        Assert.Equal(nameof(ShopResult.NotActive), buy.Status);

        var post = await PostAsync(c, store, Seller);
        Assert.False(post.Ok);
        Assert.Equal(nameof(ShopResult.NotActive), post.Status);

        // Reopen → purchases work again.
        Assert.True((await EditStoreHandler.Handle(
            new EditStore(Guild, Seller, store, Closed: false), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.True((await BuyAsync(c, listing, Buyer)).Ok);
    }

    // ---- Store types (admin taxonomy) --------------------------------------

    [Fact]
    public async Task StoreType_AssignClearAndValidate()
    {
        var c = await SeededAsync();
        var (r, typeId) = await c.Shop.CreateStoreTypeAsync(Guild, "Weapons", 0);
        Assert.Equal(ShopResult.Ok, r);
        var store = (await CreateStoreAsync(c, Seller)).Value;

        // Assign a valid type.
        Assert.True((await EditStoreHandler.Handle(new EditStore(Guild, Seller, store, StoreTypeId: typeId), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Equal(typeId, (await c.Db.ShopStores.SingleAsync()).StoreTypeId);

        // Unknown type id → rejected.
        var bad = await EditStoreHandler.Handle(new EditStore(Guild, Seller, store, StoreTypeId: Guid.NewGuid()), c.Db, c.Authz, c.Shop, default);
        Assert.False(bad.Ok);
        Assert.Equal(typeId, (await c.Db.ShopStores.SingleAsync()).StoreTypeId); // unchanged

        // Guid.Empty clears it.
        Assert.True((await EditStoreHandler.Handle(new EditStore(Guild, Seller, store, StoreTypeId: Guid.Empty), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Null((await c.Db.ShopStores.SingleAsync()).StoreTypeId);
    }

    [Fact]
    public async Task StoreType_DeletedTypeDetachesStores()
    {
        var c = await SeededAsync();
        var (_, typeId) = await c.Shop.CreateStoreTypeAsync(Guild, "Weapons", 0);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        await EditStoreHandler.Handle(new EditStore(Guild, Seller, store, StoreTypeId: typeId), c.Db, c.Authz, c.Shop, default);

        Assert.True((await DeleteStoreTypeHandler.Handle(new DeleteStoreType(Guild, Master, typeId!.Value), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Null((await c.Db.ShopStores.SingleAsync()).StoreTypeId);
        Assert.Empty(await c.Db.ShopStoreTypes.ToListAsync());
    }

    [Fact]
    public async Task Manager_CanSetTypeOnAnotherUsersStore()
    {
        var c = await SeededAsync();
        var (_, typeId) = await c.Shop.CreateStoreTypeAsync(Guild, "Weapons", 0);
        var store = (await CreateStoreAsync(c, Seller)).Value; // owned by Seller

        // Master (owner ⇒ admin ⇒ shop manager) edits Seller's store for moderation.
        Assert.True((await EditStoreHandler.Handle(new EditStore(Guild, Master, store, StoreTypeId: typeId), c.Db, c.Authz, c.Shop, default)).Ok);
        Assert.Equal(typeId, (await c.Db.ShopStores.SingleAsync()).StoreTypeId);
    }

    [Fact]
    public async Task StoreType_NonManager_CannotCreate()
    {
        var c = await SeededAsync();
        var result = await CreateStoreTypeHandler.Handle(new CreateStoreType(Guild, Seller, "Weapons"), c.Authz, c.Shop, default);
        Assert.False(result.Ok); // ShopCreator (Seller) isn't a manager/admin — taxonomy is admin-gated
        Assert.Equal(nameof(ShopResult.Forbidden), result.Status);
    }

    // ---- Default seeding ---------------------------------------------------

    [Fact]
    public async Task Provisioning_SeedsDefaultCategoriesAndTypes()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(Guild, "G", null, ownerId: Master); // seedDefaults defaults true

        Assert.True(await db.ShopCategories.AnyAsync());
        Assert.True(await db.ShopStoreTypes.AnyAsync());
        Assert.True(await db.Currencies.AnyAsync(x => x.Code == CurrencyCodes.CoinCode && x.IsSpendable));
        Assert.True(await db.MusterTemplates.AnyAsync());
        Assert.Equal(GuildSeed.CurrentVersion, (await db.FindGuildAsync(Guild))!.SeedVersion);
    }

    [Fact]
    public async Task SeedDefaults_IsAdminGated_AndIdempotent()
    {
        var c = await SeededAsync(); // harness seeds nothing
        Assert.Empty(await c.Db.ShopCategories.ToListAsync());
        var seed = new GuildSeedService(c.Db);

        // Non-admin (ShopCreator) is refused.
        var forbidden = await SeedGuildDefaultsHandler.Handle(new SeedGuildDefaults(Guild, Seller, true), c.Authz, seed, default);
        Assert.False(forbidden.Ok);

        // Admin restores defaults.
        Assert.True((await SeedGuildDefaultsHandler.Handle(new SeedGuildDefaults(Guild, Master, true), c.Authz, seed, default)).Ok);
        var cats = await c.Db.ShopCategories.CountAsync();
        var types = await c.Db.ShopStoreTypes.CountAsync();
        Assert.True(cats >= 10);
        Assert.True(types >= 10);

        // Idempotent — running again adds nothing.
        Assert.True((await SeedGuildDefaultsHandler.Handle(new SeedGuildDefaults(Guild, Master, true), c.Authz, seed, default)).Ok);
        Assert.Equal(cats, await c.Db.ShopCategories.CountAsync());
        Assert.Equal(types, await c.Db.ShopStoreTypes.CountAsync());
    }

    // ---- Delist attribution (moderator takedown vs self-withdrawal) --------

    private static Task<Result> CancelListingAsync(Ctx c, Guid listingId, ulong actor, string? reason = null)
        => CancelListingHandler.Handle(new CancelListing(Guild, actor, listingId, reason), c.Db, c.Authz, c.Shop, default);

    [Fact]
    public async Task ModeratorTakedown_RecordsWhoAndReason()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller)).Value;

        Assert.True((await CancelListingAsync(c, listing, Master, "Prohibited item")).Ok); // Master = admin/manager

        var l = await c.Db.ShopListings.SingleAsync();
        Assert.Equal(Master, l.DelistedBy);
        Assert.Equal("Prohibited item", l.DelistReason);

        var detail = await c.Reads.GetListingDetailAsync(Guild, listing);
        Assert.True(detail!.Moderated);
        Assert.Equal("Prohibited item", detail.DelistReason);
    }

    [Fact]
    public async Task SellerWithdrawal_IsNotMarkedModerated()
    {
        var c = await SeededAsync();
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller)).Value;

        Assert.True((await CancelListingAsync(c, listing, Seller)).Ok);

        var detail = await c.Reads.GetListingDetailAsync(Guild, listing);
        Assert.False(detail!.Moderated);
        Assert.Equal(Seller, (await c.Db.ShopListings.SingleAsync()).DelistedBy);
    }

    // ---- Orphan image sweep ------------------------------------------------

    [Fact]
    public async Task OrphanSweep_DeletesUnreferencedBlobsOnly()
    {
        var blobs = new FakeImageStore();
        var c = await SeededAsync(images: blobs);
        var store = (await CreateStoreAsync(c, Seller)).Value;
        var listing = (await PostAsync(c, store, Seller)).Value;

        // Attach a live image to the listing — its blob must survive the sweep.
        var l = await c.Db.ShopListings.SingleAsync(x => x.Id == listing);
        l.ImageKey = "referenced.png";
        await c.Db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var old = now.AddHours(-2);
        blobs.Add("referenced.png", old);     // referenced + old  → keep
        blobs.Add("orphan.png", old);         // unreferenced + old → delete
        blobs.Add("fresh-orphan.png", now);   // unreferenced but inside grace window → keep

        var purged = await c.Shop.SweepOrphanImagesAsync(now);

        Assert.Equal(1, purged);
        Assert.True(blobs.Contains("referenced.png"));
        Assert.False(blobs.Contains("orphan.png"));
        Assert.True(blobs.Contains("fresh-orphan.png")); // grace window protects in-flight uploads
    }

    /// <summary>In-memory <see cref="IShopImageService"/> for the orphan sweep — tracks blob keys with a creation
    /// time so <see cref="ListKeysAsync"/> can honour the grace cutoff. Only the sweep's surface is implemented.</summary>
    private sealed class FakeImageStore : IShopImageService
    {
        private readonly Dictionary<string, DateTimeOffset> _blobs = new(StringComparer.Ordinal);

        public void Add(string key, DateTimeOffset createdAt) => _blobs[key] = createdAt;
        public bool Contains(string key) => _blobs.ContainsKey(key);

        public Task<IReadOnlyList<string>> ListKeysAsync(DateTimeOffset createdBefore, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                _blobs.Where(b => b.Value <= createdBefore).Select(b => b.Key).ToList());

        public Task DeleteAsync(string? key, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(key)) { _blobs.Remove(key); }
            return Task.CompletedTask;
        }

        public Task<(ShopImageUploadResult Result, string? Key)> UploadAsync(
            Stream content, long length, string? contentType, ShopImageKind kind, CancellationToken ct = default)
            => Task.FromResult<(ShopImageUploadResult, string?)>((ShopImageUploadResult.Empty, null));

        public Task<(Stream Content, string ContentType)?> OpenAsync(string key, CancellationToken ct = default)
            => Task.FromResult<(Stream, string)?>(null);

        public Task<string?> CopyAsync(string? srcKey, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
