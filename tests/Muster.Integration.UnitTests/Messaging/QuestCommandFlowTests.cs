using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Messaging;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Quests;
using Muster.IntegrationTests.TestSupport;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// End-to-end lifecycles driven through the CQRS command handlers (the single funnel: load → authorize →
/// service). Complements <see cref="QuestCommandHandlerTests"/> (auth/guard edge cases) by walking the full
/// happy + recovery paths for both funding models — the shape the bot, web, and API all invoke.
/// </summary>
public class QuestCommandFlowTests
{
    private const ulong Guild = 1, Master = 1, Poster = 10, Worker = 20, Other = 30;

    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>().UseInMemoryDatabase($"muster-{Guid.NewGuid()}").Options);

    private sealed record Ctx(MusterDbContext Db, GuildAuthorizationService Auth, IQuestAuthorizer Authz, IQuestService Quests, Currency Coin);

    private static async Task<Ctx> SeededAsync(Action<QuestSettings>? configure = null)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(Guild, "G", null, ownerId: Master, seedDefaults: false); // owner ⇒ GuildMaster
        var guild = await db.Guilds.SingleAsync();
        configure?.Invoke(guild.Settings.Quests);
        // Quest settings moved to their own table; mirror the configured legacy values into the row the read sites use.
        db.GuildQuestSettings.Add(Muster.Domain.Entities.Guilds.GuildQuestSettings.FromLegacy(guild.Id, guild.Settings.Quests));
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = Guild, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var auth = new GuildAuthorizationService(db);
        var quests = new QuestService(db, new CurrencyService(db, new RecordingMessageBus()), auth, new RecordingMessageBus());
        return new Ctx(db, auth, new QuestAuthorizer(auth, TestSupport.TestFeatureGates.AlwaysOn), quests, coin);
    }

    private static Task FundAsync(Ctx c, ulong userId, long amount) =>
        new CurrencyService(c.Db, new RecordingMessageBus()).AwardAsync(Guild, userId, c.Coin.Id, amount, CurrencyLedgerSource.Connector, null, "seed");

    private static async Task<long> BalanceAsync(Ctx c, ulong userId) =>
        await c.Db.CurrencyLedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == c.Coin.Id && e.SeasonId == null).SumAsync(e => (long?)e.Amount) ?? 0;

    private static async Task<GuildQuest> ReloadAsync(Ctx c, Guid id) =>
        await c.Db.Quests.AsNoTracking().Include(q => q.Participants).SingleAsync(q => q.Id == id);

    private static Task<Result<Guid>> PostAsync(Ctx c, QuestOrigin origin, ulong actor, long reward = 40, QuestTier tier = QuestTier.None, bool requestFinal = false) =>
        PostQuestHandler.Handle(
            new PostQuest(Guild, actor, origin, "Quest", "COIN", reward, "brief", null, null, tier, requestFinal, 1),
            c.Db, c.Auth, c.Quests, TestSupport.TestFeatureGates.AlwaysOn, default);

    private static Task<GuildQuest> TheQuestAsync(Ctx c) => c.Db.Quests.AsNoTracking().Include(q => q.Participants).FirstAsync();

    // ---- Guild quests ------------------------------------------------------

    [Fact]
    public async Task Guild_PostClaimSubmitApprove_MintsReward()
    {
        var c = await SeededAsync();
        Assert.True((await PostAsync(c, QuestOrigin.Guild, Master, reward: 75)).Ok);
        var quest = await TheQuestAsync(c);

        Assert.True((await ClaimQuestHandler.Handle(new ClaimQuest(Guild, quest.Id, Worker), c.Db, c.Authz, c.Quests, default)).Ok);
        Assert.True((await SubmitQuestHandler.Handle(new SubmitQuest(Guild, quest.Id, Worker, "done"), c.Db, c.Authz, c.Quests, default)).Ok);
        Assert.True((await ApproveQuestSubmissionHandler.Handle(new ApproveQuestSubmission(Guild, quest.Id, Worker, Master), c.Db, c.Authz, c.Quests, default)).Ok);

        Assert.Equal(75, await BalanceAsync(c, Worker));                       // minted
        Assert.Equal(QuestStatus.Closed, (await ReloadAsync(c, quest.Id)).Status); // capacity 1 reached
    }

    [Fact]
    public async Task Guild_Reject_AwardsNothing_AndStaysOpen()
    {
        var c = await SeededAsync();
        await PostAsync(c, QuestOrigin.Guild, Master);
        var quest = await TheQuestAsync(c);
        await ClaimQuestHandler.Handle(new ClaimQuest(Guild, quest.Id, Worker), c.Db, c.Authz, c.Quests, default);
        await SubmitQuestHandler.Handle(new SubmitQuest(Guild, quest.Id, Worker, null), c.Db, c.Authz, c.Quests, default);

        var result = await RejectQuestSubmissionHandler.Handle(new RejectQuestSubmission(Guild, quest.Id, Worker, Master, "off-spec"), c.Db, c.Authz, c.Quests, default);

        Assert.True(result.Ok);
        Assert.Equal(0, await BalanceAsync(c, Worker));
        var reloaded = await ReloadAsync(c, quest.Id);
        Assert.Equal(QuestStatus.Open, reloaded.Status);                       // open for another taker
        var p = reloaded.Participants.Single(x => x.UserId == Worker);
        Assert.Equal(QuestParticipantStatus.Rejected, p.Status);
        Assert.Equal("off-spec", p.ReviewNote);                                // reject reason recorded
    }

    [Fact]
    public async Task Guild_RequestRevision_ThenResubmit_ThenApprove()
    {
        var c = await SeededAsync();
        await PostAsync(c, QuestOrigin.Guild, Master, reward: 50);
        var quest = await TheQuestAsync(c);
        await ClaimQuestHandler.Handle(new ClaimQuest(Guild, quest.Id, Worker), c.Db, c.Authz, c.Quests, default);
        await SubmitQuestHandler.Handle(new SubmitQuest(Guild, quest.Id, Worker, null), c.Db, c.Authz, c.Quests, default);

        // Manager sends member's submission back (member-targeted revise).
        Assert.True((await RequestQuestRevisionHandler.Handle(new RequestQuestRevision(Guild, quest.Id, Master, Worker, "add detail"), c.Db, c.Authz, c.Quests, default)).Ok);
        Assert.Equal(QuestParticipantStatus.RevisionRequested, (await ReloadAsync(c, quest.Id)).Participants.Single().Status);

        Assert.True((await SubmitQuestHandler.Handle(new SubmitQuest(Guild, quest.Id, Worker, "fixed"), c.Db, c.Authz, c.Quests, default)).Ok);
        Assert.True((await ApproveQuestSubmissionHandler.Handle(new ApproveQuestSubmission(Guild, quest.Id, Worker, Master), c.Db, c.Authz, c.Quests, default)).Ok);
        Assert.Equal(50, await BalanceAsync(c, Worker));
    }

    [Fact]
    public async Task Guild_Approve_WithNote_RecordsFeedback()
    {
        var c = await SeededAsync();
        await PostAsync(c, QuestOrigin.Guild, Master);
        var quest = await TheQuestAsync(c);
        await ClaimQuestHandler.Handle(new ClaimQuest(Guild, quest.Id, Worker), c.Db, c.Authz, c.Quests, default);
        await SubmitQuestHandler.Handle(new SubmitQuest(Guild, quest.Id, Worker, null), c.Db, c.Authz, c.Quests, default);

        await ApproveQuestSubmissionHandler.Handle(new ApproveQuestSubmission(Guild, quest.Id, Worker, Master, "stellar work"), c.Db, c.Authz, c.Quests, default);

        Assert.Equal("stellar work", (await ReloadAsync(c, quest.Id)).Participants.Single().ReviewNote);
    }

    [Fact]
    public async Task Guild_Cancel_ByManager_Closes()
    {
        var c = await SeededAsync();
        await PostAsync(c, QuestOrigin.Guild, Master);
        var quest = await TheQuestAsync(c);

        Assert.True((await CancelQuestHandler.Handle(new CancelQuest(Guild, quest.Id, Master), c.Db, c.Authz, c.Quests, default)).Ok);
        Assert.Equal(QuestStatus.Cancelled, (await ReloadAsync(c, quest.Id)).Status);
    }

    [Fact]
    public async Task Guild_Edit_ByManager_UpdatesFields()
    {
        var c = await SeededAsync();
        await PostAsync(c, QuestOrigin.Guild, Master, reward: 40);
        var quest = await TheQuestAsync(c);

        var result = await EditQuestHandler.Handle(
            new EditQuest(Guild, quest.Id, Master, "Renamed", "new brief", 90, null, QuestTier.A, 3), c.Db, c.Authz, c.Quests, default);

        Assert.True(result.Ok);
        var edited = await ReloadAsync(c, quest.Id);
        Assert.Equal("Renamed", edited.Name);
        Assert.Equal(90, edited.RewardAmount);
        Assert.Equal(3, edited.Capacity);
    }

    // ---- Player bounties ---------------------------------------------------

    [Fact]
    public async Task Player_Confirm_PaysCompleter_FromEscrow()
    {
        var c = await SeededAsync(s => s.PersonalQuestIntakeApproval = false);
        await FundAsync(c, Poster, 100);
        Assert.True((await PostAsync(c, QuestOrigin.Player, Poster, reward: 40)).Ok);
        var quest = await TheQuestAsync(c);
        Assert.Equal(60, await BalanceAsync(c, Poster));                       // escrow held

        await ClaimQuestHandler.Handle(new ClaimQuest(Guild, quest.Id, Worker), c.Db, c.Authz, c.Quests, default);
        await SubmitQuestHandler.Handle(new SubmitQuest(Guild, quest.Id, Worker, null), c.Db, c.Authz, c.Quests, default);
        Assert.True((await ConfirmQuestHandler.Handle(new ConfirmQuest(Guild, quest.Id, Poster), c.Db, c.Authz, c.Quests, default)).Ok);

        Assert.Equal(40, await BalanceAsync(c, Worker));                       // completer paid from escrow
        Assert.Equal(QuestStatus.Closed, (await ReloadAsync(c, quest.Id)).Status);
    }

    [Fact]
    public async Task Player_Intake_Accept_OpensAndAssignsTier()
    {
        var c = await SeededAsync(s => s.PersonalQuestIntakeApproval = true);
        await FundAsync(c, Poster, 100);
        await PostAsync(c, QuestOrigin.Player, Poster, reward: 40);
        var quest = await TheQuestAsync(c);
        Assert.Equal(QuestStatus.PendingApproval, quest.Status);

        var result = await AcceptQuestIntakeHandler.Handle(new AcceptQuestIntake(Guild, quest.Id, Master, QuestTier.B, false), c.Db, c.Authz, c.Quests, default);

        Assert.True(result.Ok);
        var opened = await ReloadAsync(c, quest.Id);
        Assert.Equal(QuestStatus.Open, opened.Status);
        Assert.Equal(QuestTier.B, opened.Tier);
    }

    [Fact]
    public async Task Player_Intake_Reject_RefundsOwner()
    {
        var c = await SeededAsync(s => s.PersonalQuestIntakeApproval = true);
        await FundAsync(c, Poster, 100);
        await PostAsync(c, QuestOrigin.Player, Poster, reward: 40);
        var quest = await TheQuestAsync(c);
        Assert.Equal(60, await BalanceAsync(c, Poster)); // held

        Assert.True((await RejectQuestIntakeHandler.Handle(new RejectQuestIntake(Guild, quest.Id, Master), c.Db, c.Authz, c.Quests, default)).Ok);

        Assert.Equal(100, await BalanceAsync(c, Poster)); // refunded
        Assert.NotEqual(QuestStatus.Open, (await ReloadAsync(c, quest.Id)).Status);
    }

    [Fact]
    public async Task Player_Dispute_Arbitrate_PaysCompleter()
    {
        var c = await SeededAsync(s => s.PersonalQuestIntakeApproval = false);
        await FundAsync(c, Poster, 100);
        await PostAsync(c, QuestOrigin.Player, Poster, reward: 40);
        var quest = await TheQuestAsync(c);
        await ClaimQuestHandler.Handle(new ClaimQuest(Guild, quest.Id, Worker), c.Db, c.Authz, c.Quests, default);
        await SubmitQuestHandler.Handle(new SubmitQuest(Guild, quest.Id, Worker, null), c.Db, c.Authz, c.Quests, default);

        // Owner raises the dispute; the GuildMaster (not a party) arbitrates in the completer's favour.
        Assert.True((await DisputeQuestHandler.Handle(new DisputeQuest(Guild, quest.Id, Poster), c.Db, c.Authz, c.Quests, default)).Ok);
        Assert.Equal(QuestStatus.Disputed, (await ReloadAsync(c, quest.Id)).Status);

        Assert.True((await ArbitrateQuestHandler.Handle(new ArbitrateQuest(Guild, quest.Id, Master, Pay: true), c.Db, c.Authz, c.Quests, default)).Ok);
        Assert.Equal(40, await BalanceAsync(c, Worker));
    }

    [Fact]
    public async Task Player_Finalize_Pay_AfterOwnerConfirm()
    {
        var c = await SeededAsync(s => { s.PersonalQuestIntakeApproval = false; s.FinalApprovalMode = FinalApprovalMode.Forced; });
        await FundAsync(c, Poster, 100);
        await PostAsync(c, QuestOrigin.Player, Poster, reward: 40);
        var quest = await TheQuestAsync(c);
        await ClaimQuestHandler.Handle(new ClaimQuest(Guild, quest.Id, Worker), c.Db, c.Authz, c.Quests, default);
        await SubmitQuestHandler.Handle(new SubmitQuest(Guild, quest.Id, Worker, null), c.Db, c.Authz, c.Quests, default);

        await ConfirmQuestHandler.Handle(new ConfirmQuest(Guild, quest.Id, Poster), c.Db, c.Authz, c.Quests, default);
        Assert.Equal(QuestStatus.PendingFinal, (await ReloadAsync(c, quest.Id)).Status); // owner accepted, awaiting sign-off

        Assert.True((await FinalizeQuestHandler.Handle(new FinalizeQuest(Guild, quest.Id, Master, Pay: true), c.Db, c.Authz, c.Quests, default)).Ok);
        Assert.Equal(40, await BalanceAsync(c, Worker));
        Assert.Equal(QuestStatus.Closed, (await ReloadAsync(c, quest.Id)).Status);
    }

    [Fact]
    public async Task Player_PostWithZeroReward_IsRejected()
    {
        var c = await SeededAsync(s => s.PersonalQuestIntakeApproval = false);
        await FundAsync(c, Poster, 100);

        var result = await PostAsync(c, QuestOrigin.Player, Poster, reward: 0);

        Assert.False(result.Ok);
        Assert.Equal(0, await c.Db.Quests.CountAsync());
    }
}
