using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;

namespace Muster.IntegrationTests;

public class EscrowServiceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<(MusterDbContext db, Currency coin)> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();
        return (db, coin);
    }

    private static Task<long> BalanceAsync(MusterDbContext db, ulong userId, Guid currencyId) =>
        db.CurrencyLedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == null)
            .SumAsync(e => (long?)e.Amount).ContinueWith(t => t.Result ?? 0);

    private static async Task FundAsync(MusterDbContext db, CurrencyService awards, Currency coin, ulong userId, long amount)
        => await awards.AwardAsync(1, userId, coin.Id, amount, CurrencyLedgerSource.Connector, null, "seed");

    [Fact]
    public async Task Hold_DebitsOwner_CreditsEscrow_AndCommitsWithStateInOneTransaction()
    {
        var (db, coin) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        await FundAsync(db, awards, coin, 10, 100);

        var sut = awards;

        var status = await sut.HoldAsync(1, ownerId: 10, coin.Id, 40, "bounty:b1");
        // Caller commits the escrow legs together with the state change (here: simulated).
        await db.SaveChangesAsync();

        Assert.Equal(EscrowStatus.Ok, status);
        Assert.Equal(60, await BalanceAsync(db, 10, coin.Id));
        Assert.Equal(40, await BalanceAsync(db, CurrencyService.EscrowAccountUserId, coin.Id));
    }

    [Fact]
    public async Task Hold_Rejects_InsufficientFunds_And_NonSpendable()
    {
        var (db, coin) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        await FundAsync(db, awards, coin, 10, 5);
        var sut = awards;

        Assert.Equal(EscrowStatus.InsufficientFunds, await sut.HoldAsync(1, 10, coin.Id, 40, "b"));

        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        Assert.Equal(EscrowStatus.NotSpendable, await sut.HoldAsync(1, 10, points.Id, 1, "b2"));
    }

    [Fact]
    public async Task Hold_Then_Payout_TransfersToCompleter_ZeroSum()
    {
        var (db, coin) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        await FundAsync(db, awards, coin, 10, 100);
        var sut = awards;

        await sut.HoldAsync(1, 10, coin.Id, 40, "bounty:b1");
        await db.SaveChangesAsync();
        await sut.PayoutAsync(1, completerId: 20, coin.Id, 40, "bounty:b1");
        await db.SaveChangesAsync();

        Assert.Equal(60, await BalanceAsync(db, 10, coin.Id));   // owner kept the rest
        Assert.Equal(40, await BalanceAsync(db, 20, coin.Id));   // completer paid
        Assert.Equal(0, await BalanceAsync(db, CurrencyService.EscrowAccountUserId, coin.Id)); // escrow emptied
    }

    [Fact]
    public async Task Hold_Then_Refund_RestoresOwner()
    {
        var (db, coin) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        await FundAsync(db, awards, coin, 10, 100);
        var sut = awards;

        await sut.HoldAsync(1, 10, coin.Id, 40, "bounty:b1");
        await db.SaveChangesAsync();
        await sut.RefundAsync(1, 10, coin.Id, 40, "bounty:b1");
        await db.SaveChangesAsync();

        Assert.Equal(100, await BalanceAsync(db, 10, coin.Id));
        Assert.Equal(0, await BalanceAsync(db, CurrencyService.EscrowAccountUserId, coin.Id));
    }

    [Fact]
    public async Task Hold_IsIdempotent_OnSourceKey()
    {
        var (db, coin) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        await FundAsync(db, awards, coin, 10, 100);
        var sut = awards;

        await sut.HoldAsync(1, 10, coin.Id, 40, "bounty:b1");
        await db.SaveChangesAsync();
        await sut.HoldAsync(1, 10, coin.Id, 40, "bounty:b1"); // retry, same key
        await db.SaveChangesAsync();

        Assert.Equal(60, await BalanceAsync(db, 10, coin.Id)); // debited once, not twice
    }
}
