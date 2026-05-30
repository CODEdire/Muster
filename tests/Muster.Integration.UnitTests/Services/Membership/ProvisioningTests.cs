using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;

namespace Muster.IntegrationTests;

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

        db.CurrencyLedgerEntries.AddRange(
            new CurrencyLedgerEntry { GuildId = 1, UserId = 10, CurrencyId = points.Id, SeasonId = season.Id, Amount = 30, SourceType = CurrencyLedgerSource.ManualAward },
            new CurrencyLedgerEntry { GuildId = 1, UserId = 10, CurrencyId = points.Id, SeasonId = season.Id, Amount = 20, SourceType = CurrencyLedgerSource.Muster },
            new CurrencyLedgerEntry { GuildId = 1, UserId = 99, CurrencyId = points.Id, SeasonId = season.Id, Amount = 40, SourceType = CurrencyLedgerSource.Quest });
        await db.SaveChangesAsync();

        // GetWallets is a display read off the wallet cache; build the cache from the ledger first.
        await new CurrencyService(db, new RecordingMessageBus()).RebuildWalletsAsync(1);

        var wallets = await new CurrencyReadService(db).GetWalletsAsync(1, userId: 10);

        var pointsWallet = Assert.Single(wallets, w => w.CurrencyCode == "POINTS");
        Assert.True(pointsWallet.IsSeasonal);
        Assert.Equal(50, pointsWallet.Balance); // only user 10's entries
    }
}
