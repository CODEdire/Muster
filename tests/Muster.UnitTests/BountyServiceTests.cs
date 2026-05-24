using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

public class BountyServiceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private sealed record Ctx(MusterDbContext Db, BountyService Sut, Currency Coin);

    private static async Task<Ctx> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var awards = new AwardService(db);
        var auth = new GuildAuthorizationService(db);
        var sut = new BountyService(db, new EscrowService(db, awards), auth);
        return new Ctx(db, sut, coin);
    }

    private static Task<long> BalanceAsync(MusterDbContext db, ulong userId, Guid currencyId) =>
        db.LedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == null)
            .SumAsync(e => (long?)e.Amount).ContinueWith(t => t.Result ?? 0);

    private static async Task FundAsync(Ctx c, ulong userId, long amount)
        => await new AwardService(c.Db).AwardAsync(1, userId, c.Coin.Id, amount, LedgerSourceType.Connector, null, "seed");

    [Fact]
    public async Task Post_EscrowsReward_AndCreatesOpenBounty()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);

        var (result, mission) = await c.Sut.PostAsync(1, 10, "Mine ore", "200 SCU", c.Coin.Id, 40);

        Assert.Equal(BountyResult.Ok, result);
        Assert.Equal(MissionStatus.Open, mission!.Status);
        Assert.Equal(MissionOrigin.Player, mission.Origin);
        Assert.Equal(60, await BalanceAsync(c.Db, 10, c.Coin.Id));   // escrowed
        Assert.Equal(40, await BalanceAsync(c.Db, EscrowService.EscrowAccountUserId, c.Coin.Id));
    }

    [Fact]
    public async Task Post_InsufficientFunds_DoesNotCreateBounty()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 5);

        var (result, mission) = await c.Sut.PostAsync(1, 10, "Mine ore", "", c.Coin.Id, 40);

        Assert.Equal(BountyResult.InsufficientFunds, result);
        Assert.Null(mission);
        Assert.Equal(0, await c.Db.Missions.CountAsync());
        Assert.Equal(5, await BalanceAsync(c.Db, 10, c.Coin.Id)); // untouched
    }

    [Fact]
    public async Task FullFlow_Take_Submit_Confirm_PaysCompleter()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Sut.PostAsync(1, 10, "Mine ore", "", c.Coin.Id, 40);
        var id = m!.Id;

        Assert.Equal(BountyResult.Forbidden, await c.Sut.TakeAsync(id, 10)); // owner can't take own
        Assert.Equal(BountyResult.Ok, await c.Sut.TakeAsync(id, 20));
        Assert.Equal(BountyResult.InvalidState, await c.Sut.TakeAsync(id, 30)); // already taken
        Assert.Equal(BountyResult.Ok, await c.Sut.SubmitAsync(id, 20));
        Assert.Equal(BountyResult.Ok, await c.Sut.ConfirmAsync(id, 10));

        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id));  // completer paid
        Assert.Equal(60, await BalanceAsync(c.Db, 10, c.Coin.Id));  // owner kept the rest
        Assert.Equal(0, await BalanceAsync(c.Db, EscrowService.EscrowAccountUserId, c.Coin.Id));
        Assert.Equal(MissionStatus.Closed, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Cancel_BeforeSubmission_Refunds()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Sut.PostAsync(1, 10, "Mine ore", "", c.Coin.Id, 40);

        await c.Sut.TakeAsync(m!.Id, 20);
        Assert.Equal(BountyResult.Ok, await c.Sut.CancelAsync(m.Id, 10));

        Assert.Equal(100, await BalanceAsync(c.Db, 10, c.Coin.Id)); // fully refunded
        Assert.Equal(MissionStatus.Cancelled, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Dispute_ThenArbitrate_CanPayOrRefund()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Sut.PostAsync(1, 10, "Mine ore", "", c.Coin.Id, 40);
        await c.Sut.TakeAsync(m!.Id, 20);
        await c.Sut.SubmitAsync(m.Id, 20);

        Assert.Equal(BountyResult.Ok, await c.Sut.DisputeAsync(m.Id, 10));
        Assert.Equal(MissionStatus.Disputed, (await c.Db.Missions.SingleAsync()).Status);

        Assert.Equal(BountyResult.Ok, await c.Sut.ArbitrateAsync(m.Id, payCompleter: true));
        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id));
        Assert.Equal(MissionStatus.Closed, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Expire_RefundsOpenPastDeadlineBounties()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);
        await c.Sut.PostAsync(1, 10, "Mine ore", "", c.Coin.Id, 40, deadline: DateTimeOffset.UtcNow.AddHours(-1));

        var expired = await c.Sut.ExpireDueAsync(1, DateTimeOffset.UtcNow);

        Assert.Equal(1, expired);
        Assert.Equal(100, await BalanceAsync(c.Db, 10, c.Coin.Id));
        Assert.Equal(MissionStatus.Expired, (await c.Db.Missions.SingleAsync()).Status);
    }
}
