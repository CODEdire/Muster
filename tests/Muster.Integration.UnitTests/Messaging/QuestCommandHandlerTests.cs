using Muster.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Messaging;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Quests;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// The CQRS command handler is the single funnel: it authorizes (resource-based) before delegating to the
/// quest service. These exercise the handler directly (as Wolverine would call it).
/// </summary>
public class QuestCommandHandlerTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>().UseInMemoryDatabase($"muster-{Guid.NewGuid()}").Options);

    private sealed record Ctx(MusterDbContext Db, IQuestAuthorizer Authz, IQuestService Quests, Currency Points, RecordingMessageBus Bus);

    private static async Task<Ctx> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1); // owner 1 ⇒ GuildMaster
        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var auth = new GuildAuthorizationService(db);
        var bus = new RecordingMessageBus();
        return new Ctx(db, new QuestAuthorizer(auth), new QuestService(db, new CurrencyService(db, new RecordingMessageBus()), auth, bus), points, bus);
    }

    private static async Task<GuildQuest> SubmittedGuildQuestAsync(Ctx c)
    {
        var quest = (await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Recruit", "", c.Points.Id, 100))).Quest!;
        await c.Quests.ClaimAsync(quest.Id, 10);
        await c.Quests.SubmitAsync(quest.Id, 10);
        return quest;
    }

    [Fact]
    public async Task Approve_ByGuildMaster_AwardsTheMember()
    {
        var c = await SeededAsync();
        var quest = await SubmittedGuildQuestAsync(c);

        var result = await ApproveQuestSubmissionHandler.Handle(
            new ApproveQuestSubmission(1, quest.Id, MemberId: 10, ReviewerId: 1), c.Db, c.Authz, c.Quests, default);

        Assert.True(result.Ok);
        Assert.Equal(100, (await c.Db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
    }

    [Fact]
    public async Task Approve_WithMismatchedGuildId_IsNotFound_AndAwardsNothing()
    {
        var c = await SeededAsync();
        var quest = await SubmittedGuildQuestAsync(c); // belongs to guild 1

        // Same quest id, but routed through a different guild — the handler scopes the lookup to the guild, so the
        // quest isn't found and no cross-guild action (or mis-attributed audit) can occur.
        var result = await ApproveQuestSubmissionHandler.Handle(
            new ApproveQuestSubmission(999, quest.Id, MemberId: 10, ReviewerId: 1), c.Db, c.Authz, c.Quests, default);

        Assert.False(result.Ok);
        Assert.Equal(nameof(QuestResult.NotFound), result.Status);
        Assert.False(await c.Db.Wallets.AnyAsync(w => w.UserId == 10 && w.Balance != 0));
    }

    [Fact]
    public async Task Approve_PublishesSettledLifecycleEvent()
    {
        var c = await SeededAsync();
        var quest = await SubmittedGuildQuestAsync(c);

        await ApproveQuestSubmissionHandler.Handle(
            new ApproveQuestSubmission(1, quest.Id, MemberId: 10, ReviewerId: 1), c.Db, c.Authz, c.Quests, default);

        // The transition emits its lifecycle event on the bus (a consumer / outbox delivers it downstream).
        Assert.Contains(c.Bus.Published.OfType<QuestLifecycleNotified>(),
            e => e.QuestId == quest.Id && e.Moment == QuestLifecycleMoment.Settled);
    }

    [Fact]
    public async Task Approve_ByNonManager_IsForbidden_AndAwardsNothing()
    {
        var c = await SeededAsync();
        var quest = await SubmittedGuildQuestAsync(c);

        var result = await ApproveQuestSubmissionHandler.Handle(
            new ApproveQuestSubmission(1, quest.Id, MemberId: 10, ReviewerId: 99), c.Db, c.Authz, c.Quests, default); // 99 has no role

        Assert.False(result.Ok);
        Assert.Equal(nameof(QuestResult.Forbidden), result.Status);
        Assert.Equal(0, await c.Db.CurrencyLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Approve_OnPlayerBounty_IsRejected_ByOriginGuard()
    {
        var c = await SeededAsync();
        // A player-origin quest with a submitted taker, inserted directly (no escrow needed for this guard test).
        var bounty = GuildQuest.Create(new QuestDraft(1, 2, QuestOrigin.Player, "Bounty", "", c.Points.Id, 50));
        var taker = QuestParticipant.NewClaim(bounty.Id, 10);
        taker.Submit(null);
        bounty.Participants.Add(taker);
        c.Db.Quests.Add(bounty);
        await c.Db.SaveChangesAsync();

        var result = await ApproveQuestSubmissionHandler.Handle(
            new ApproveQuestSubmission(1, bounty.Id, MemberId: 10, ReviewerId: 1), c.Db, c.Authz, c.Quests, default);

        Assert.False(result.Ok);
        Assert.Equal(nameof(QuestResult.InvalidState), result.Status); // mint path refuses player bounties
    }

    [Fact]
    public async Task Reject_ByNonManager_IsForbidden()
    {
        var c = await SeededAsync();
        var quest = await SubmittedGuildQuestAsync(c);

        var result = await RejectQuestSubmissionHandler.Handle(
            new RejectQuestSubmission(1, quest.Id, MemberId: 10, ReviewerId: 99), c.Db, c.Authz, c.Quests, default);

        Assert.False(result.Ok);
        Assert.Equal(nameof(QuestResult.Forbidden), result.Status);
        Assert.Equal(QuestParticipantStatus.Submitted, (await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 10)).Status); // untouched
    }

    [Fact]
    public async Task Reopen_ByGuildMaster_RestoresSubmission()
    {
        var c = await SeededAsync();
        var quest = await SubmittedGuildQuestAsync(c);
        await c.Quests.RejectAsync(quest.Id, 10, reviewerId: 1);

        var result = await ReopenQuestRejectionHandler.Handle(
            new ReopenQuestRejection(1, quest.Id, MemberId: 10, ReviewerId: 1), c.Db, c.Authz, c.Quests, default);

        Assert.True(result.Ok);
        Assert.Equal(QuestParticipantStatus.Submitted, (await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 10)).Status);
    }

    [Fact]
    public async Task Post_GuildQuest_ByNonManager_IsRefused()
    {
        var c = await SeededAsync();
        c.Db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true });
        await c.Db.SaveChangesAsync();

        var result = await PostQuestHandler.Handle(
            new PostQuest(1, ActorId: 99, QuestOrigin.Guild, "Drive", "COIN", 10, "", null, null, QuestTier.None, false, 1),
            c.Db, new GuildAuthorizationService(c.Db), c.Quests, default);

        Assert.False(result.Ok);
        Assert.Contains("quest manager", result.Status); // validation message surfaced verbatim
        Assert.Equal(0, await c.Db.Quests.CountAsync());
    }

    [Fact]
    public async Task Arbitrate_ByAPartyManager_IsRefused_ByRecusal()
    {
        var c = await SeededAsync();
        // The guild owner (id 1) is a GuildMaster but also the bounty's owner — recusal must block them.
        var bounty = GuildQuest.Create(new QuestDraft(1, 1, QuestOrigin.Player, "Bounty", "", c.Points.Id, 50));
        bounty.DisputedBy = 1;
        bounty.TransitionTo(QuestStatus.Open);
        var taker = QuestParticipant.NewClaim(bounty.Id, 20);
        taker.Submit(null);
        bounty.Participants.Add(taker);
        bounty.TransitionTo(QuestStatus.Disputed);
        c.Db.Quests.Add(bounty);
        await c.Db.SaveChangesAsync();

        var result = await ArbitrateQuestHandler.Handle(
            new ArbitrateQuest(1, bounty.Id, ActorId: 1, Pay: true), c.Db, c.Authz, c.Quests, default);

        Assert.False(result.Ok);
        Assert.Equal(nameof(QuestResult.Forbidden), result.Status); // judge can't rule on their own case
    }

    [Fact]
    public async Task ReleaseClaim_SelfAbandon_FreesSlot_AndPublishesReleased()
    {
        var c = await SeededAsync();
        var quest = (await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Patrol", "", c.Points.Id, 10))).Quest!;
        await c.Quests.ClaimAsync(quest.Id, 10);

        var result = await ReleaseQuestClaimHandler.Handle(
            new ReleaseQuestClaim(1, quest.Id, ActorId: 10, TargetUserId: 10), c.Db, c.Authz, c.Quests, default);

        Assert.True(result.Ok);
        Assert.Equal(QuestParticipantStatus.Released, (await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 10)).Status);
        Assert.Contains(c.Bus.Published.OfType<QuestLifecycleNotified>(),
            e => e.QuestId == quest.Id && e.Moment == QuestLifecycleMoment.Released && e.TargetUserId == 10);
    }

    [Fact]
    public async Task ReleaseClaim_ForceRelease_ByManager_FreesAnothersSlot()
    {
        var c = await SeededAsync();
        var quest = (await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Patrol", "", c.Points.Id, 10))).Quest!;
        await c.Quests.ClaimAsync(quest.Id, 10);

        var result = await ReleaseQuestClaimHandler.Handle( // manager (owner id 1) releases member 10
            new ReleaseQuestClaim(1, quest.Id, ActorId: 1, TargetUserId: 10), c.Db, c.Authz, c.Quests, default);

        Assert.True(result.Ok);
        Assert.Equal(QuestParticipantStatus.Released, (await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 10)).Status);
    }

    [Fact]
    public async Task ReleaseClaim_ForceRelease_ByNonManager_IsForbidden()
    {
        var c = await SeededAsync();
        var quest = (await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Patrol", "", c.Points.Id, 10))).Quest!;
        await c.Quests.ClaimAsync(quest.Id, 10);

        var result = await ReleaseQuestClaimHandler.Handle( // 99 is neither the taker nor a manager
            new ReleaseQuestClaim(1, quest.Id, ActorId: 99, TargetUserId: 10), c.Db, c.Authz, c.Quests, default);

        Assert.False(result.Ok);
        Assert.Equal(nameof(QuestResult.Forbidden), result.Status);
        Assert.Equal(QuestParticipantStatus.Claimed, (await c.Db.QuestParticipants.SingleAsync(p => p.UserId == 10)).Status); // untouched
    }

    [Fact]
    public async Task ReleaseClaim_SelfAbandon_ByNonParticipant_IsForbidden()
    {
        var c = await SeededAsync();
        var quest = (await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Patrol", "", c.Points.Id, 10))).Quest!;

        var result = await ReleaseQuestClaimHandler.Handle( // 50 never claimed → not a taker
            new ReleaseQuestClaim(1, quest.Id, ActorId: 50, TargetUserId: 50), c.Db, c.Authz, c.Quests, default);

        Assert.False(result.Ok);
        Assert.Equal(nameof(QuestResult.Forbidden), result.Status);
    }

    [Fact]
    public async Task Claim_AtPerUserLimit_IsRefused()
    {
        var c = await SeededAsync();
        var guild = await c.Db.Guilds.SingleAsync();
        guild.Settings.Quests.MaxActiveClaimsPerUser = 1;
        await c.Db.SaveChangesAsync();

        var q1 = (await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "A", "", c.Points.Id, 10))).Quest!;
        var q2 = (await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "B", "", c.Points.Id, 10))).Quest!;
        await c.Quests.ClaimAsync(q1.Id, 10); // one active claim

        var result = await ClaimQuestHandler.Handle(new ClaimQuest(1, q2.Id, UserId: 10), c.Db, c.Authz, c.Quests, default);

        Assert.False(result.Ok);
        Assert.Equal(nameof(QuestResult.AtClaimLimit), result.Status);
    }
}
