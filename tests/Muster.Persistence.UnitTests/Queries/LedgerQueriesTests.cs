using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.Persistence.UnitTests.Queries;

public class CurrencyLedgerQueriesTests
{
    private static CurrencyLedgerEntry Entry(Guid currencyId, ulong userId, long amount, Guid? seasonId) => new()
    {
        GuildId = 1,
        UserId = userId,
        CurrencyId = currencyId,
        SeasonId = seasonId,
        Amount = amount,
        SourceType = CurrencyLedgerSource.Quest,
        SourceId = null,
        OccurredAt = DateTimeOffset.UtcNow,
        Reason = "t",
    };

    [Fact]
    public async Task BalanceAsync_SumsOnlyTheRequestedSeasonScope()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var coin = Guid.NewGuid();
        var season = Guid.NewGuid();

        db.CurrencyLedgerEntries.AddRange(
            Entry(coin, 10, 50, seasonId: null),
            Entry(coin, 10, -10, seasonId: null),
            Entry(coin, 10, 999, seasonId: season)); // seasonal — excluded from the non-seasonal scope
        await db.SaveChangesAsync();

        Assert.Equal(40, await db.BalanceAsync(1, 10, coin, seasonId: null));
        Assert.Equal(999, await db.BalanceAsync(1, 10, coin, seasonId: season));
        Assert.Equal(0, await db.BalanceAsync(1, 20, coin, seasonId: null)); // different user
    }

    [Fact]
    public async Task CurrencySupplyAsync_SplitsCirculationFromEscrow_AndCountsHolders()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var coin = Guid.NewGuid();
        const ulong escrow = 0;

        // Two members hold balances; the house/escrow account holds 30; one debit removes 10.
        db.CurrencyLedgerEntries.AddRange(
            Entry(coin, 10, 100, null),
            Entry(coin, 20, 50, null),
            Entry(coin, 10, -10, null),
            Entry(coin, escrow, 30, null));
        // Holders count reads the wallet cache, so seed matching wallets.
        db.Wallets.AddRange(
            new Wallet { GuildId = 1, UserId = 10, CurrencyId = coin, SeasonId = null, Balance = 90 },
            new Wallet { GuildId = 1, UserId = 20, CurrencyId = coin, SeasonId = null, Balance = 50 },
            new Wallet { GuildId = 1, UserId = escrow, CurrencyId = coin, SeasonId = null, Balance = 30 });
        await db.SaveChangesAsync();

        var supply = await db.CurrencySupplyAsync(1, coin, seasonId: null, escrowUserId: escrow);

        Assert.Equal(180, supply.GrossCredited);   // 100 + 50 + 30
        Assert.Equal(10, supply.GrossDebited);      // |−10|
        Assert.Equal(140, supply.Circulating);      // net 170 − 30 escrow
        Assert.Equal(30, supply.Escrow);
        Assert.Equal(2, supply.Holders);            // members 10 & 20; escrow excluded
    }

    [Fact]
    public async Task WalletColumnAsync_ReturnsBalancesForScope_KeyedByUser()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var coin = Guid.NewGuid();
        var other = Guid.NewGuid();

        db.Wallets.AddRange(
            new Wallet { GuildId = 1, UserId = 10, CurrencyId = coin, SeasonId = null, Balance = 90 },
            new Wallet { GuildId = 1, UserId = 20, CurrencyId = coin, SeasonId = null, Balance = 50 },
            new Wallet { GuildId = 1, UserId = 10, CurrencyId = other, SeasonId = null, Balance = 7 }); // different currency
        await db.SaveChangesAsync();

        var col = await db.WalletColumnAsync(1, coin, seasonId: null);

        Assert.Equal(2, col.Count);
        Assert.Equal(90, col[10]);
        Assert.Equal(50, col[20]);
    }

    [Fact]
    public async Task GuildLedgerAsync_NewestFirst_FiltersByCurrency()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var coin = Guid.NewGuid();
        var gem = Guid.NewGuid();

        db.CurrencyLedgerEntries.AddRange(
            Entry(coin, 10, 100, null),
            Entry(gem, 20, 5, null),
            Entry(coin, 30, 200, null));
        await db.SaveChangesAsync();

        var all = await db.GuildLedgerAsync(1, currencyId: null, skip: 0, take: 50);
        Assert.Equal(3, all.Count);
        Assert.Equal(30ul, all[0].UserId); // newest (highest Id) first

        var coinOnly = await db.GuildLedgerAsync(1, coin, skip: 0, take: 50);
        Assert.Equal(2, coinOnly.Count);
        Assert.All(coinOnly, r => Assert.Equal(coin, r.CurrencyId));
    }

    [Fact]
    public async Task TopByCurrencyAsync_RanksDescending_AndLimits()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var coin = Guid.NewGuid();

        db.CurrencyLedgerEntries.AddRange(
            Entry(coin, 10, 100, null),
            Entry(coin, 20, 50, null),
            Entry(coin, 30, 200, null));
        await db.SaveChangesAsync();

        var top = await db.TopByCurrencyAsync(1, coin, seasonId: null, top: 2);

        Assert.Equal(2, top.Count);
        Assert.Equal((30ul, 200L), top[0]);
        Assert.Equal((10ul, 100L), top[1]);
    }
}
