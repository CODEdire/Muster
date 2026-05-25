using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

public class QuestMaintenanceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private sealed record Ctx(MusterDbContext Db, BountyService Bounties, MissionService Missions, QuestMaintenanceService Maint, Currency Coin);

    private static async Task<Ctx> SeededAsync(Action<GuildSettings> configure)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var guild = await db.Guilds.SingleAsync();
        configure(guild.Settings);
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var awards = new AwardService(db);
        var auth = new GuildAuthorizationService(db);
        var bounties = new BountyService(db, new EscrowService(db, awards), auth, awards, new NullQuestNotifier(), new NullQuestRewardSink());
        var missions = new MissionService(db, awards, auth, new NullQuestNotifier(), new NullQuestRewardSink());
        var maint = new QuestMaintenanceService(db, bounties, missions, new AuditService(db),
            new LoggingQuestNotifier(NullLogger<LoggingQuestNotifier>.Instance), NullLogger<QuestMaintenanceService>.Instance);
        return new Ctx(db, bounties, missions, maint, coin);
    }

    private static Task FundAsync(Ctx c, ulong userId, long amount)
        => new AwardService(c.Db).AwardAsync(1, userId, c.Coin.Id, amount, LedgerSourceType.Connector, null, "seed");

    private static async Task<long> BalanceAsync(MusterDbContext db, ulong userId, Guid currencyId)
        => await db.LedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == null)
            .SumAsync(e => (long?)e.Amount) ?? 0;

    private static async Task BackdateStatusAsync(Ctx c, Guid missionId, TimeSpan ago)
    {
        var m = await c.Db.Missions.SingleAsync(x => x.Id == missionId);
        m.StatusChangedAt = DateTimeOffset.UtcNow - ago;
        await c.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task IntakeTimeout_Decline_RefundsAndCancels()
    {
        var c = await SeededAsync(s => { s.PersonalQuestIntakeApproval = true; s.IntakeTimeoutHours = 1; s.IntakeTimeoutAction = StaleIntakeAction.Decline; });
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Bounties.PostAsync(1, 10, "Escort", "", c.Coin.Id, 40, requireIntake: true);
        Assert.Equal(MissionStatus.PendingApproval, m!.Status);
        Assert.Equal(60, await BalanceAsync(c.Db, 10, c.Coin.Id)); // escrowed

        await BackdateStatusAsync(c, m.Id, TimeSpan.FromHours(2));
        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));

        Assert.Equal(MissionStatus.Cancelled, (await c.Db.Missions.SingleAsync()).Status);
        Assert.Equal(100, await BalanceAsync(c.Db, 10, c.Coin.Id)); // refunded
    }

    [Fact]
    public async Task SubmissionTimeout_Approve_PaysCompleter()
    {
        var c = await SeededAsync(s => { s.SubmissionTimeoutHours = 1; s.SubmissionTimeoutAction = StaleSubmissionAction.Approve; });
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Bounties.PostAsync(1, 10, "Escort", "", c.Coin.Id, 40);
        await c.Bounties.TakeAsync(m!.Id, 20);
        await c.Bounties.SubmitAsync(m.Id, 20);
        var taker = await c.Db.MissionParticipants.SingleAsync(p => p.UserId == 20);
        taker.SubmittedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await c.Db.SaveChangesAsync();

        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id));
        Assert.Equal(MissionStatus.Closed, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task ClaimTimeout_ReleasesIdleTaker()
    {
        var c = await SeededAsync(s => s.ClaimTimeoutHours = 1);
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Bounties.PostAsync(1, 10, "Escort", "", c.Coin.Id, 40);
        await c.Bounties.TakeAsync(m!.Id, 20);
        var taker = await c.Db.MissionParticipants.SingleAsync(p => p.UserId == 20);
        taker.ClaimedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await c.Db.SaveChangesAsync();

        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(MissionParticipantStatus.Rejected, (await c.Db.MissionParticipants.SingleAsync(p => p.UserId == 20)).Status);
        Assert.Equal(MissionStatus.Open, (await c.Db.Missions.SingleAsync()).Status); // reclaimable
    }

    [Fact]
    public async Task FinalTimeout_Approve_Pays()
    {
        var c = await SeededAsync(s => { s.FinalApprovalTimeoutHours = 1; s.FinalApprovalTimeoutAction = StaleFinalAction.Approve; });
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Bounties.PostAsync(1, 10, "Escort", "", c.Coin.Id, 40, requireFinalApproval: true);
        await c.Bounties.TakeAsync(m!.Id, 20);
        await c.Bounties.SubmitAsync(m.Id, 20);
        await c.Bounties.ConfirmAsync(m.Id, 10); // owner accepts → PendingFinal
        Assert.Equal(MissionStatus.PendingFinal, (await c.Db.Missions.SingleAsync()).Status);

        await BackdateStatusAsync(c, m.Id, TimeSpan.FromHours(2));
        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id));
        Assert.Equal(MissionStatus.Closed, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Sweep_LeavesFreshQuestsAlone()
    {
        var c = await SeededAsync(s => { s.PersonalQuestIntakeApproval = true; s.IntakeTimeoutHours = 24; s.IntakeTimeoutAction = StaleIntakeAction.Decline; });
        await FundAsync(c, 10, 100);
        await c.Bounties.PostAsync(1, 10, "Escort", "", c.Coin.Id, 40, requireIntake: true);

        Assert.Equal(0, await c.Maint.SweepAsync(DateTimeOffset.UtcNow)); // posted just now, well within 24h
        Assert.Equal(MissionStatus.PendingApproval, (await c.Db.Missions.SingleAsync()).Status);
    }
}
