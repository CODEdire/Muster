using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.Persistence.UnitTests.Queries;

public class LedgerQueriesTests
{
    private static LedgerEntry Entry(Guid currencyId, ulong userId, long amount, Guid? seasonId) => new()
    {
        GuildId = 1,
        UserId = userId,
        CurrencyId = currencyId,
        SeasonId = seasonId,
        Amount = amount,
        SourceType = LedgerSourceType.Quest,
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

        db.LedgerEntries.AddRange(
            Entry(coin, 10, 50, seasonId: null),
            Entry(coin, 10, -10, seasonId: null),
            Entry(coin, 10, 999, seasonId: season)); // seasonal — excluded from the non-seasonal scope
        await db.SaveChangesAsync();

        Assert.Equal(40, await db.BalanceAsync(1, 10, coin, seasonId: null));
        Assert.Equal(999, await db.BalanceAsync(1, 10, coin, seasonId: season));
        Assert.Equal(0, await db.BalanceAsync(1, 20, coin, seasonId: null)); // different user
    }

    [Fact]
    public async Task TopByCurrencyAsync_RanksDescending_AndLimits()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var coin = Guid.NewGuid();

        db.LedgerEntries.AddRange(
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
