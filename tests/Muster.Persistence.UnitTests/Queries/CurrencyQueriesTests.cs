using Muster.Domain;
using Muster.Domain.Entities;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.Persistence.UnitTests.Queries;

public class CurrencyQueriesTests
{
    [Fact]
    public async Task FindCurrencyAsync_NormalizesCode()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true });
        await db.SaveChangesAsync();

        Assert.NotNull(await db.FindCurrencyAsync(1, " coin "));   // trimmed + upper-cased
        Assert.Null(await db.FindCurrencyAsync(1, "NOPE"));
    }

    [Fact]
    public async Task FindPointsAsync_MatchesTheWellKnownCode()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = CurrencyCodes.PointsCode, Name = "Points", IsSeasonal = true });
        await db.SaveChangesAsync();

        var points = await db.FindPointsAsync(1);

        Assert.NotNull(points);
        Assert.Equal(CurrencyCodes.PointsCode, points!.Code);
    }

    [Fact]
    public async Task FindCurrencyByIdAsync_ScopesToTheGuild()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var id = Guid.NewGuid();
        db.Currencies.Add(new Currency { Id = id, GuildId = 1, Code = "COIN", Name = "Coin" });
        await db.SaveChangesAsync();

        Assert.NotNull(await db.FindCurrencyByIdAsync(1, id));
        Assert.Null(await db.FindCurrencyByIdAsync(2, id)); // right id, wrong guild
    }

    [Fact]
    public async Task CurrencyCodeMapAsync_MapsIdToCodeForTheGuild()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var coin = Guid.NewGuid();
        db.Currencies.AddRange(
            new Currency { Id = coin, GuildId = 1, Code = "COIN", Name = "Coin" },
            new Currency { Id = Guid.NewGuid(), GuildId = 2, Code = "GEM", Name = "Gem" }); // other guild excluded
        await db.SaveChangesAsync();

        var map = await db.CurrencyCodeMapAsync(1);

        Assert.Equal("COIN", Assert.Contains(coin, map));
    }

    [Fact]
    public async Task CurrencyExistsAsync_DetectsTakenCodes()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin" });
        await db.SaveChangesAsync();

        Assert.True(await db.CurrencyExistsAsync(1, "COIN"));
        Assert.False(await db.CurrencyExistsAsync(1, "GEM"));
        Assert.False(await db.CurrencyExistsAsync(2, "COIN")); // different guild
    }
}
