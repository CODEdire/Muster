using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Currencies;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Seasons;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.IntegrationTests;

public class PointsEngagementTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static PointsReadService Sut(MusterDbContext db)
        => new(db, new CurrencyReadService(db), new ParticipationReadService(db));

    private static void Earn(MusterDbContext db, ulong guildId, Guid currencyId, Guid seasonId, ulong userId, long amount)
        => db.CurrencyLedgerEntries.Add(new CurrencyLedgerEntry
        {
            GuildId = guildId,
            UserId = userId,
            CurrencyId = currencyId,
            SeasonId = seasonId,
            Amount = amount,
            SourceType = CurrencyLedgerSource.TrackingSession,
            OccurredAt = DateTimeOffset.UtcNow,
            Reason = "test",
        });

    [Fact]
    public async Task GetEngagement_ComputesNewReturningRetentionAndParticipation()
    {
        using var db = NewDb();
        // User ids stay above the escrow (0) / burn (1) sentinels, which the earned-sum queries exclude.
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 11);

        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        points.IsSeasonal = true;
        await db.SaveChangesAsync();

        // Ten members make the participation-rate denominator.
        var sync = new MemberSyncService(db);
        for (ulong u = 11; u <= 20; u++)
        {
            await sync.UpsertAsync(1, u, $"u{u}", null, null);
        }

        // Season 1 (provisioned, active) then Season 2 (archives S1, opens S2).
        var seasons = new SeasonService(db);
        var s2 = await seasons.StartAsync(1, "Season 2");
        var s1Id = (await db.SeasonsAsync(1)).Single(s => s.Name != "Season 2").Id;

        // S1 earners {11,12,13}; S2 earners {12,13,14,15}.
        Earn(db, 1, points.Id, s1Id, 11, 100);
        Earn(db, 1, points.Id, s1Id, 12, 200);
        Earn(db, 1, points.Id, s1Id, 13, 300);
        Earn(db, 1, points.Id, s2.Id, 12, 40);
        Earn(db, 1, points.Id, s2.Id, 13, 60);
        Earn(db, 1, points.Id, s2.Id, 14, 80);
        Earn(db, 1, points.Id, s2.Id, 15, 20);
        await db.SaveChangesAsync();

        var home = await Sut(db).GetEngagementAsync(1);

        Assert.Equal(2, home.Seasons.Count);
        Assert.Equal(800, home.LifetimeAwarded);
        Assert.Equal(5, home.MembersEverEarned);   // union {1,2,3,4,5}
        Assert.Equal(10, home.MemberCount);

        var s1 = home.Seasons[0];
        Assert.Equal(3, s1.Earners);
        Assert.Equal(3, s1.NewEarners);
        Assert.Equal(0, s1.ReturningEarners);
        Assert.Equal(30, s1.ParticipationPct);

        var s2e = home.Seasons[1];
        Assert.Equal(4, s2e.Earners);
        Assert.Equal(2, s2e.NewEarners);          // {4,5}
        Assert.Equal(2, s2e.ReturningEarners);    // {2,3}
        Assert.Equal(3, s2e.PriorEarners);
        Assert.Equal(67, s2e.RetentionPct);       // 2 / 3
        Assert.Equal(40, s2e.ParticipationPct);

        Assert.Equal(s2.Id, home.Current!.Season.Id);   // active
        Assert.Equal(s1Id, home.Previous!.Season.Id);
    }
}
