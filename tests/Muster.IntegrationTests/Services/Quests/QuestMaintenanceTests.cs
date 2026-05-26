using Muster.Contracts;
using Muster.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;

namespace Muster.IntegrationTests;

public class QuestMaintenanceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private sealed record Ctx(MusterDbContext Db, QuestService Quests, QuestMaintenanceService Maint, Currency Coin);

    private static async Task<Ctx> SeededAsync(Action<QuestSettings> configure)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var guild = await db.Guilds.SingleAsync();
        configure(guild.Settings.Quests);
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var awards = new CurrencyService(db, new NullCurrencyEventSink());
        var auth = new GuildAuthorizationService(db);
        var quests = new QuestService(db, awards, auth, new RecordingMessageBus());
        var maint = new QuestMaintenanceService(db, quests, new AuditService(db),
            NullLogger<QuestMaintenanceService>.Instance);
        return new Ctx(db, quests, maint, coin);
    }

    private static Task FundAsync(Ctx c, ulong userId, long amount)
        => new CurrencyService(c.Db, new NullCurrencyEventSink()).AwardAsync(1, userId, c.Coin.Id, amount, LedgerSourceType.Connector, null, "seed");

    private static async Task<long> BalanceAsync(MusterDbContext db, ulong userId, Guid currencyId)
        => await db.LedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == null)
            .SumAsync(e => (long?)e.Amount) ?? 0;

    private static async Task BackdateStatusAsync(Ctx c, Guid missionId, TimeSpan ago)
    {
        var m = await c.Db.Quests.SingleAsync(x => x.Id == missionId);
        m.StatusChangedAt = DateTimeOffset.UtcNow - ago;
        await c.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task IntakeTimeout_Decline_RefundsAndCancels()
    {
        var c = await SeededAsync(s => { s.PersonalQuestIntakeApproval = true; s.IntakeTimeoutHours = 1; s.IntakeTimeoutAction = StaleIntakeAction.Decline; });
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Quests.PostQuestAsync(new QuestDraft(1, 10, QuestOrigin.Player, "Escort", "", c.Coin.Id, 40, RequireIntake: true));
        Assert.Equal(QuestStatus.PendingApproval, m!.Status);
        Assert.Equal(60, await BalanceAsync(c.Db, 10, c.Coin.Id)); // escrowed

        await BackdateStatusAsync(c, m.Id, TimeSpan.FromHours(2));
        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));

        Assert.Equal(QuestStatus.Cancelled, (await c.Db.Quests.SingleAsync()).Status);
        Assert.Equal(100, await BalanceAsync(c.Db, 10, c.Coin.Id)); // refunded
    }

    [Fact]
    public async Task SubmissionTimeout_Approve_PaysCompleter()
    {
        var c = await SeededAsync(s => { s.SubmissionTimeoutHours = 1; s.SubmissionTimeoutAction = StaleSubmissionAction.Approve; });
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Quests.PostQuestAsync(new QuestDraft(1, 10, QuestOrigin.Player, "Escort", "", c.Coin.Id, 40));
        await c.Quests.ClaimAsync(m!.Id, 20);
        await c.Quests.SubmitAsync(m.Id, 20);
        var taker = await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 20);
        taker.SubmittedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await c.Db.SaveChangesAsync();

        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id));
        Assert.Equal(QuestStatus.Closed, (await c.Db.Quests.SingleAsync()).Status);
    }

    [Fact]
    public async Task ClaimTimeout_ReleasesIdleTaker()
    {
        var c = await SeededAsync(s => s.ClaimTimeoutHours = 1);
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Quests.PostQuestAsync(new QuestDraft(1, 10, QuestOrigin.Player, "Escort", "", c.Coin.Id, 40));
        await c.Quests.ClaimAsync(m!.Id, 20);
        var taker = await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 20);
        taker.ClaimedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await c.Db.SaveChangesAsync();

        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(QuestParticipantStatus.Released, (await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 20)).Status); // released, not rejected
        Assert.Equal(QuestStatus.Open, (await c.Db.Quests.SingleAsync()).Status); // reclaimable
    }

    [Fact]
    public async Task FinalTimeout_Approve_Pays()
    {
        var c = await SeededAsync(s => { s.FinalApprovalTimeoutHours = 1; s.FinalApprovalTimeoutAction = StaleFinalAction.Approve; });
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Quests.PostQuestAsync(new QuestDraft(1, 10, QuestOrigin.Player, "Escort", "", c.Coin.Id, 40, RequireFinalApproval: true));
        await c.Quests.ClaimAsync(m!.Id, 20);
        await c.Quests.SubmitAsync(m.Id, 20);
        await c.Quests.ConfirmAsync(m.Id, 10); // owner accepts → PendingFinal
        Assert.Equal(QuestStatus.PendingFinal, (await c.Db.Quests.SingleAsync()).Status);

        await BackdateStatusAsync(c, m.Id, TimeSpan.FromHours(2));
        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id));
        Assert.Equal(QuestStatus.Closed, (await c.Db.Quests.SingleAsync()).Status);
    }

    [Fact]
    public async Task Sweep_LeavesFreshQuestsAlone()
    {
        var c = await SeededAsync(s => { s.PersonalQuestIntakeApproval = true; s.IntakeTimeoutHours = 24; s.IntakeTimeoutAction = StaleIntakeAction.Decline; });
        await FundAsync(c, 10, 100);
        await c.Quests.PostQuestAsync(new QuestDraft(1, 10, QuestOrigin.Player, "Escort", "", c.Coin.Id, 40, RequireIntake: true));

        Assert.Equal(0, await c.Maint.SweepAsync(DateTimeOffset.UtcNow)); // posted just now, well within 24h
        Assert.Equal(QuestStatus.PendingApproval, (await c.Db.Quests.SingleAsync()).Status);
    }

    [Fact]
    public async Task DisputeTimeout_OwnerRaised_PaysCompleter()
    {
        // Burden on the disputant: the owner raised it and didn't follow through ⇒ the completer is paid.
        var c = await SeededAsync(s => s.DisputeTimeoutHours = 1);
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Quests.PostQuestAsync(new QuestDraft(1, 10, QuestOrigin.Player, "Escort", "", c.Coin.Id, 40));
        await c.Quests.ClaimAsync(m!.Id, 20);
        await c.Quests.SubmitAsync(m.Id, 20);
        await c.Quests.DisputeAsync(m.Id, 10); // owner-raised
        await BackdateStatusAsync(c, m.Id, TimeSpan.FromHours(2));

        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id)); // completer paid
        Assert.Equal(QuestStatus.Closed, (await c.Db.Quests.SingleAsync()).Status);
    }

    [Fact]
    public async Task GuildSubmissionTimeout_Approve_MintsAndCloses()
    {
        // Guild quests can't be disputed; an idle submission past the cutoff is auto-approved (mints the reward).
        var c = await SeededAsync(s => { s.SubmissionTimeoutHours = 1; s.SubmissionTimeoutAction = StaleSubmissionAction.Approve; });
        var (_, m) = await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Drive", "", c.Coin.Id, 40));
        await c.Quests.ClaimAsync(m!.Id, 20);
        await c.Quests.SubmitAsync(m.Id, 20);
        var taker = await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 20);
        taker.SubmittedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await c.Db.SaveChangesAsync();

        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id)); // minted
        Assert.Equal(QuestStatus.Closed, (await c.Db.Quests.SingleAsync()).Status);
    }

    [Fact]
    public async Task GuildClaimTimeout_ReleasesIdleTaker_QuestStaysOpen()
    {
        var c = await SeededAsync(s => s.ClaimTimeoutHours = 1);
        var (_, m) = await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Drive", "", c.Coin.Id, 40));
        await c.Quests.ClaimAsync(m!.Id, 20);
        var taker = await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 20);
        taker.ClaimedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await c.Db.SaveChangesAsync();

        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(QuestParticipantStatus.Released, (await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 20)).Status);
        Assert.Equal(QuestStatus.Open, (await c.Db.Quests.SingleAsync()).Status);
    }

    [Fact]
    public async Task DisputeTimeout_TakerRaised_RefundsOwner()
    {
        // The taker raised it and didn't follow through ⇒ the owner is refunded.
        var c = await SeededAsync(s => s.DisputeTimeoutHours = 1);
        await FundAsync(c, 10, 100);
        var (_, m) = await c.Quests.PostQuestAsync(new QuestDraft(1, 10, QuestOrigin.Player, "Escort", "", c.Coin.Id, 40));
        await c.Quests.ClaimAsync(m!.Id, 20);
        await c.Quests.SubmitAsync(m.Id, 20);
        await c.Quests.DisputeAsync(m.Id, 20); // taker-raised
        await BackdateStatusAsync(c, m.Id, TimeSpan.FromHours(2));

        Assert.Equal(1, await c.Maint.SweepAsync(DateTimeOffset.UtcNow));
        Assert.Equal(0, await BalanceAsync(c.Db, 20, c.Coin.Id)); // completer not paid
        Assert.Equal(100, await BalanceAsync(c.Db, 10, c.Coin.Id)); // owner refunded
        Assert.Equal(QuestStatus.Cancelled, (await c.Db.Quests.SingleAsync()).Status);
    }
}
