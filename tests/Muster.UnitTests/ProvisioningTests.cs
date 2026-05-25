using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

public class ProvisioningTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task EnsureGuild_SeedsPointsAndSeason_AndIsIdempotent()
    {
        using var db = NewDb();
        var sut = new GuildProvisioningService(db);

        await sut.EnsureGuildAsync(123, "Test Guild", null);
        await sut.EnsureGuildAsync(123, "Renamed Guild", "icon"); // second call must not duplicate

        Assert.Equal(1, await db.Guilds.CountAsync());
        Assert.Equal("Renamed Guild", (await db.Guilds.SingleAsync()).Name);
        Assert.Equal(1, await db.Currencies.CountAsync(c => c.GuildId == 123 && c.Code == "POINTS"));
        Assert.Equal(1, await db.Seasons.CountAsync(s => s.GuildId == 123 && s.Status == SeasonStatus.Active));
    }

    [Fact]
    public async Task Wallets_SumSeasonalPointsForActiveSeason()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null);

        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var season = await db.Seasons.SingleAsync(s => s.Status == SeasonStatus.Active);

        db.LedgerEntries.AddRange(
            new LedgerEntry { GuildId = 1, UserId = 10, CurrencyId = points.Id, SeasonId = season.Id, Amount = 30, SourceType = LedgerSourceType.ManualAward },
            new LedgerEntry { GuildId = 1, UserId = 10, CurrencyId = points.Id, SeasonId = season.Id, Amount = 20, SourceType = LedgerSourceType.Muster },
            new LedgerEntry { GuildId = 1, UserId = 99, CurrencyId = points.Id, SeasonId = season.Id, Amount = 40, SourceType = LedgerSourceType.Mission });
        await db.SaveChangesAsync();

        var wallets = await new ScoreQueryService(db).GetWalletsAsync(1, userId: 10);

        var pointsWallet = Assert.Single(wallets, w => w.CurrencyCode == "POINTS");
        Assert.True(pointsWallet.IsSeasonal);
        Assert.Equal(50, pointsWallet.Balance); // only user 10's entries
    }
}
