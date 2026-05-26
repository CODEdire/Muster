using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Muster.Domain;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands.Membership;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Seasons;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.IntegrationTests;

public class CurrencyRetentionConfigTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static ConfigCommandService Config(MusterDbContext db, int globalMax) =>
        new(db, Options.Create(new CurrencyRetentionOptions { MaxLedgerRetentionDays = globalMax }));

    [Fact]
    public async Task SetLedgerRetention_RejectsValuesAbovePlatformCap()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null);
        var sut = Config(db, globalMax: 30);

        var tooLong = await sut.SetLedgerRetentionAsync(1, days: 60);
        Assert.True(tooLong.IsError);
        Assert.Contains("30", tooLong.Message);

        var ok = await sut.SetLedgerRetentionAsync(1, days: 10);
        Assert.False(ok.IsError);
        var guild = await db.FindGuildAsync(1);
        Assert.Equal(10, guild!.Settings.LedgerRetentionDays);
    }

    [Fact]
    public async Task SeasonArchive_FoldsSeasonHistoryIntoCheckpoint_PreservingBalance()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null);
        var points = await db.FindPointsAsync(1);
        var season = await db.FindActiveSeasonAsync(1);

        var currency = new CurrencyService(db, new RecordingMessageBus());
        await currency.AwardPointsAsync(1, 10, 30, CurrencyLedgerSource.Quest, "q1", "win");
        await currency.AwardPointsAsync(1, 10, 20, CurrencyLedgerSource.Muster, "m1", "muster");

        var before = await db.BalanceAsync(1, 10, points!.Id, season!.Id);
        Assert.Equal(50, before);

        var prune = new LedgerPruneService(db, Options.Create(new CurrencyRetentionOptions()), NullLogger<LedgerPruneService>.Instance);
        await new SeasonService(db, prune).EndAsync(1);

        // The archived season's two entries are folded into a single checkpoint, balance unchanged.
        var seasonEntries = await db.CurrencyLedgerEntries.Where(e => e.SeasonId == season.Id).ToListAsync();
        var checkpoint = Assert.Single(seasonEntries);
        Assert.Equal(CurrencyLedgerSource.Checkpoint, checkpoint.SourceType);
        Assert.Equal(50, await db.BalanceAsync(1, 10, points.Id, season.Id));
    }
}
