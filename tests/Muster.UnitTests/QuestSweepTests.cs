using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

public class QuestSweepTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private sealed record Ctx(MusterDbContext Db, MissionService Missions, BountyService Bounties, Currency Coin);

    private static async Task<Ctx> SeededAsync(ulong guildId)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(guildId, $"G{guildId}", null, ownerId: guildId);
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = guildId, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var awards = new AwardService(db);
        var auth = new GuildAuthorizationService(db);
        return new Ctx(db, new MissionService(db, awards, auth), new BountyService(db, new EscrowService(db, awards), auth, awards), coin);
    }

    private static Task<long> BalanceAsync(MusterDbContext db, ulong userId, Guid currencyId) =>
        db.LedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == null)
            .SumAsync(e => (long?)e.Amount).ContinueWith(t => t.Result ?? 0);

    [Fact]
    public async Task GlobalSweep_ActivatesAndExpires_AcrossGuilds()
    {
        var g1 = await SeededAsync(1);
        var g2 = await SeededAsync(2);

        // g1: a scheduled quest whose start has passed → should activate.
        await g1.Missions.CreateGuildQuestAsync(1, "Patrol", "", 1, "COIN", 50,
            startsAt: DateTimeOffset.UtcNow.AddMinutes(5));
        var scheduled = await g1.Db.Missions.SingleAsync();
        scheduled.ScheduledStart = DateTimeOffset.UtcNow.AddMinutes(-1); // now due
        await g1.Db.SaveChangesAsync();

        // g2: a personal quest past its deadline → should expire and refund.
        await new AwardService(g2.Db).AwardAsync(2, 20, g2.Coin.Id, 100, LedgerSourceType.Connector, null, "seed");
        await g2.Bounties.PostAsync(2, 20, "Escort", "", g2.Coin.Id, 40, deadline: DateTimeOffset.UtcNow.AddHours(-1));

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await g1.Missions.ActivateScheduledAsync(now));
        Assert.Equal(MissionStatus.Open, (await g1.Db.Missions.SingleAsync()).Status);

        Assert.Equal(1, await g2.Bounties.ExpireDueAsync(now));
        Assert.Equal(MissionStatus.Expired, (await g2.Db.Missions.SingleAsync()).Status);
        Assert.Equal(100, await BalanceAsync(g2.Db, 20, g2.Coin.Id)); // escrow refunded
    }

    [Fact]
    public async Task Sweep_IsIdempotent_AcrossRepeatedRuns()
    {
        var c = await SeededAsync(1);
        await new AwardService(c.Db).AwardAsync(1, 20, c.Coin.Id, 100, LedgerSourceType.Connector, null, "seed");
        await c.Bounties.PostAsync(1, 20, "Escort", "", c.Coin.Id, 40, deadline: DateTimeOffset.UtcNow.AddHours(-1));

        var now = DateTimeOffset.UtcNow;
        await c.Bounties.ExpireDueAsync(now);
        await c.Bounties.ExpireDueAsync(now);

        // A second sweep must not double-refund.
        Assert.Equal(100, await BalanceAsync(c.Db, 20, c.Coin.Id));
        Assert.Equal(MissionStatus.Expired, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task GuildQuest_PastDeadline_Expires_WithoutRefund()
    {
        var c = await SeededAsync(1);
        await c.Missions.CreateGuildQuestAsync(1, "Patrol", "", 1, "COIN", 50,
            deadline: DateTimeOffset.UtcNow.AddHours(-1));

        // The player-quest sweep must ignore it (it's guild-funded)...
        Assert.Equal(0, await c.Bounties.ExpireDueAsync(DateTimeOffset.UtcNow));
        // ...and the guild-quest sweep must close it.
        Assert.Equal(1, await c.Missions.ExpireDueQuestsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(MissionStatus.Expired, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task GuildQuest_AwaitingApproval_IsNotExpired()
    {
        var c = await SeededAsync(1);
        await c.Missions.CreateGuildQuestAsync(1, "Patrol", "", 1, "COIN", 50,
            deadline: DateTimeOffset.UtcNow.AddHours(-1));
        var quest = await c.Db.Missions.SingleAsync();
        await c.Missions.ClaimAsync(quest.Id, 20);
        await c.Missions.SubmitAsync(quest.Id, 20);

        Assert.Equal(0, await c.Missions.ExpireDueQuestsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(MissionStatus.Open, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task GlobalActivate_LeavesFutureScheduledQuestsAlone()
    {
        var c = await SeededAsync(1);
        await c.Missions.CreateGuildQuestAsync(1, "Future", "", 1, "COIN", 50,
            startsAt: DateTimeOffset.UtcNow.AddHours(2));

        Assert.Equal(0, await c.Missions.ActivateScheduledAsync(DateTimeOffset.UtcNow));
        Assert.Equal(MissionStatus.Scheduled, (await c.Db.Missions.SingleAsync()).Status);
    }
}
