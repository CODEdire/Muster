using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

public class QuestBoardAndTimeZoneTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private sealed record Ctx(MusterDbContext Db, QuestBoardService Board, MissionService Missions, TimeZoneService Tz, Currency Coin);

    private static async Task<Ctx> SeededAsync(string guildTz = "UTC")
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var guild = await db.Guilds.SingleAsync();
        guild.TimeZoneId = guildTz;
        guild.Settings.PersonalQuestIntakeApproval = false; // most flow tests use the direct open->claim path
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var awards = new AwardService(db);
        var auth = new GuildAuthorizationService(db);
        var missions = new MissionService(db, awards, auth, new NullQuestNotifier(), new NullQuestRewardSink());
        var bounties = new BountyService(db, new EscrowService(db, awards), auth, awards, new NullQuestNotifier(), new NullQuestRewardSink());
        var tz = new TimeZoneService(db);
        var quests = new QuestCommandService(missions, db);
        var bountyCmds = new BountyCommandService(bounties, db);
        var board = new QuestBoardService(db, missions, quests, bountyCmds, auth, tz);
        return new Ctx(db, board, missions, tz, coin);
    }

    private static async Task FundAsync(Ctx c, ulong userId, long amount)
        => await new AwardService(c.Db).AwardAsync(1, userId, c.Coin.Id, amount, LedgerSourceType.Connector, null, "seed");

    // --- Time zone ---

    [Fact]
    public async Task SetUserZone_ValidatesAndStores()
    {
        var c = await SeededAsync();
        Assert.True((await c.Tz.SetUserZoneAsync(10, "America/New_York")).Ok);
        Assert.Equal("America/New_York", await c.Db.Users.Where(u => u.Id == 10).Select(u => u.TimeZoneId).SingleAsync());

        var bad = await c.Tz.SetUserZoneAsync(10, "Not/AZone");
        Assert.False(bad.Ok);
    }

    [Fact]
    public async Task ResolveZone_PrefersUser_ThenGuild_ThenUtc()
    {
        var c = await SeededAsync(guildTz: "Europe/London");
        Assert.Equal("Europe/London", await c.Tz.ResolveZoneIdAsync(1, 10));   // falls back to guild

        await c.Tz.SetUserZoneAsync(10, "Asia/Tokyo");
        Assert.Equal("Asia/Tokyo", await c.Tz.ResolveZoneIdAsync(1, 10));      // user pref wins
    }

    [Fact]
    public async Task LocalToUtc_UsesResolvedZone()
    {
        var c = await SeededAsync();
        await c.Tz.SetUserZoneAsync(10, "America/New_York"); // June → EDT (UTC-4)

        var (ok, utc, _) = await c.Tz.ParseLocalAsync(1, 10, "2026-06-01 12:00");

        Assert.True(ok);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 16, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public async Task ParseLocal_BadInput_ReturnsError()
    {
        var c = await SeededAsync();
        var (ok, _, error) = await c.Tz.ParseLocalAsync(1, 10, "next thursday-ish");
        Assert.False(ok);
        Assert.NotNull(error);
    }

    // --- Scheduled start ---

    [Fact]
    public async Task PersonalQuest_WithFutureStart_IsScheduled_ThenActivates()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);
        var start = DateTimeOffset.UtcNow.AddHours(1);

        var posted = await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40, startsAt: start);
        Assert.False(posted.IsError);
        var mission = await c.Db.Missions.SingleAsync();
        Assert.Equal(MissionStatus.Scheduled, mission.Status);

        // Can't take a scheduled quest yet.
        Assert.True((await c.Board.ClaimAsync(1, mission.Id.ToString(), 20)).IsError);

        var activated = await c.Missions.ActivateScheduledAsync(1, DateTimeOffset.UtcNow.AddHours(2));
        Assert.Equal(1, activated);
        Assert.Equal(MissionStatus.Open, (await c.Db.Missions.SingleAsync()).Status);

        Assert.False((await c.Board.ClaimAsync(1, mission.Id.ToString(), 20)).IsError);
    }

    [Fact]
    public async Task GuildQuest_WithFutureStart_BlocksClaimUntilActive()
    {
        var c = await SeededAsync();
        var start = DateTimeOffset.UtcNow.AddHours(1);

        var posted = await c.Board.PostAsync(1, actorId: 1, QuestKind.Guild, "Patrol", c.Coin.Code, 50, startsAt: start);
        Assert.False(posted.IsError);
        var mission = await c.Db.Missions.SingleAsync();
        Assert.Equal(MissionStatus.Scheduled, mission.Status);

        var claim = await c.Board.ClaimAsync(1, mission.Id.ToString(), 20);
        Assert.True(claim.IsError);
        Assert.Contains("started", claim.Message);
    }

    // --- Routing by origin ---

    [Fact]
    public async Task Claim_RoutesByOrigin()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);

        await c.Board.PostAsync(1, 1, QuestKind.Guild, "Patrol", c.Coin.Code, 50);
        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);
        var guild = await c.Db.Missions.SingleAsync(m => m.Origin == MissionOrigin.Guild);
        var personal = await c.Db.Missions.SingleAsync(m => m.Origin == MissionOrigin.Player);

        Assert.False((await c.Board.ClaimAsync(1, guild.Id.ToString(), 20)).IsError);
        Assert.False((await c.Board.ClaimAsync(1, personal.Id.ToString(), 30)).IsError);

        Assert.Equal(MissionParticipantStatus.Claimed,
            (await c.Db.MissionParticipants.SingleAsync(p => p.MissionId == guild.Id)).Status);
    }

    [Fact]
    public async Task ApproveOnPersonal_AndConfirmOnGuild_GiveHelpfulErrors()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);

        await c.Board.PostAsync(1, 1, QuestKind.Guild, "Patrol", c.Coin.Code, 50);
        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);
        var guild = await c.Db.Missions.SingleAsync(m => m.Origin == MissionOrigin.Guild);
        var personal = await c.Db.Missions.SingleAsync(m => m.Origin == MissionOrigin.Player);

        var approvePersonal = await c.Board.ApproveAsync(1, personal.Id.ToString(), 20, 1);
        Assert.True(approvePersonal.IsError);
        Assert.Contains("confirm", approvePersonal.Message);

        var confirmGuild = await c.Board.ConfirmAsync(1, guild.Id.ToString(), 1);
        Assert.True(confirmGuild.IsError);
        Assert.Contains("approve", confirmGuild.Message);
    }

    [Fact]
    public async Task List_ShowsBothTypes()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);
        await c.Board.PostAsync(1, 1, QuestKind.Guild, "Patrol", c.Coin.Code, 50);
        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);

        var list = (await c.Board.ListAsync(1)).Message;

        Assert.Contains("Patrol", list);
        Assert.Contains("Escort", list);
        Assert.Contains("Guild", list);
        Assert.Contains("Personal", list);
    }

    [Fact]
    public async Task PersonalQuest_FromNonManager_IsAllowed_ButGuildQuestIsNot()
    {
        var c = await SeededAsync();
        await FundAsync(c, 50, 100);

        // user 50 is an ordinary member (not owner/admin) — personal ok, guild rejected.
        Assert.False((await c.Board.PostAsync(1, 50, QuestKind.Personal, "Escort", c.Coin.Code, 40)).IsError);

        var guild = await c.Board.PostAsync(1, 50, QuestKind.Guild, "Patrol", c.Coin.Code, 50);
        Assert.True(guild.IsError);
        Assert.Contains("quest manager", guild.Message);
    }

    // --- Tier-based bonus points ---

    private static async Task<long> BalanceAsync(MusterDbContext db, ulong userId, Guid currencyId)
        => await db.LedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == null)
            .SumAsync(e => (long?)e.Amount) ?? 0;

    private static async Task<long> PointsAsync(MusterDbContext db, ulong guildId, ulong userId)
    {
        var pointsId = await db.Currencies.Where(cur => cur.GuildId == guildId && cur.Code == "POINTS").Select(cur => cur.Id).FirstAsync();
        return await db.LedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == pointsId).SumAsync(e => (long?)e.Amount) ?? 0;
    }

    [Fact]
    public async Task GuildQuest_WithTier_AwardsBonusPointsOnApproval()
    {
        var c = await SeededAsync();
        await c.Board.PostAsync(1, 1, QuestKind.Guild, "Patrol", c.Coin.Code, 50, tier: QuestTier.B);
        var q = await c.Db.Missions.SingleAsync();
        Assert.Equal(QuestTier.B, q.Tier);
        Assert.Equal(50, q.BonusPoints); // default B tier points

        await c.Board.ClaimAsync(1, q.Id.ToString(), 20);
        await c.Board.SubmitAsync(1, q.Id.ToString(), 20);
        Assert.False((await c.Board.ApproveAsync(1, q.Id.ToString(), 20, 1)).IsError);

        Assert.Equal(50, await BalanceAsync(c.Db, 20, c.Coin.Id)); // minted currency reward
        Assert.Equal(50, await PointsAsync(c.Db, 1, 20));          // tier B bonus points
    }

    private static async Task SetApprovalAsync(Ctx c, bool intake, FinalApprovalMode mode)
    {
        var guild = await c.Db.Guilds.SingleAsync();
        guild.Settings.PersonalQuestIntakeApproval = intake;
        guild.Settings.FinalApprovalMode = mode;
        await c.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task PersonalQuest_WithIntakeApproval_RequiresAcceptBeforeClaim()
    {
        var c = await SeededAsync();
        await SetApprovalAsync(c, intake: true, FinalApprovalMode.Off);
        await FundAsync(c, 10, 100);

        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);
        var q = await c.Db.Missions.SingleAsync();
        Assert.Equal(MissionStatus.PendingApproval, q.Status);

        // Not claimable until an approver accepts it.
        Assert.True((await c.Board.ClaimAsync(1, q.Id.ToString(), 20)).IsError);

        Assert.False((await c.Board.AcceptIntakeAsync(1, q.Id.ToString(), QuestTier.C, false, 1)).IsError);
        var opened = await c.Db.Missions.SingleAsync();
        Assert.Equal(MissionStatus.Open, opened.Status);
        Assert.Equal(QuestTier.C, opened.Tier);
        Assert.Equal(30, opened.BonusPoints); // tier C default
    }

    [Fact]
    public async Task PersonalQuest_IntakeTier_PaysBonusPointsOnConfirm()
    {
        var c = await SeededAsync();
        await SetApprovalAsync(c, intake: true, FinalApprovalMode.Off);
        await FundAsync(c, 10, 100);

        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);
        var q = await c.Db.Missions.SingleAsync();
        await c.Board.AcceptIntakeAsync(1, q.Id.ToString(), QuestTier.B, false, 1);
        await c.Board.ClaimAsync(1, q.Id.ToString(), 20);
        await c.Board.SubmitAsync(1, q.Id.ToString(), 20);

        Assert.False((await c.Board.ConfirmAsync(1, q.Id.ToString(), 10)).IsError); // owner confirms

        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id)); // escrow paid
        Assert.Equal(50, await PointsAsync(c.Db, 1, 20));          // tier B default
        Assert.Equal(MissionStatus.Closed, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task PersonalQuest_RejectedAtIntake_RefundsOwner()
    {
        var c = await SeededAsync();
        await SetApprovalAsync(c, intake: true, FinalApprovalMode.Off);
        await FundAsync(c, 10, 100);

        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);
        var q = await c.Db.Missions.SingleAsync();
        Assert.Equal(60, await BalanceAsync(c.Db, 10, c.Coin.Id)); // 40 escrowed

        Assert.False((await c.Board.RejectIntakeAsync(1, q.Id.ToString(), 1)).IsError);
        Assert.Equal(100, await BalanceAsync(c.Db, 10, c.Coin.Id)); // refunded
        Assert.Equal(MissionStatus.Cancelled, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task PersonalQuest_FinalApproval_HoldsPayoutUntilManagerSignsOff()
    {
        var c = await SeededAsync();
        await SetApprovalAsync(c, intake: true, FinalApprovalMode.Forced);
        await FundAsync(c, 10, 100);

        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);
        var q = await c.Db.Missions.SingleAsync();
        Assert.True(q.RequiresFinalApproval); // forced by server
        await c.Board.AcceptIntakeAsync(1, q.Id.ToString(), QuestTier.D, false, 1);
        await c.Board.ClaimAsync(1, q.Id.ToString(), 20);
        await c.Board.SubmitAsync(1, q.Id.ToString(), 20);

        // Owner confirms, but payout is held for a manager's final sign-off.
        await c.Board.ConfirmAsync(1, q.Id.ToString(), 10);
        Assert.Equal(MissionStatus.PendingFinal, (await c.Db.Missions.SingleAsync()).Status);
        Assert.Equal(0, await BalanceAsync(c.Db, 20, c.Coin.Id)); // not paid yet

        Assert.False((await c.Board.FinalizeAsync(1, q.Id.ToString(), true, 1)).IsError);
        Assert.Equal(40, await BalanceAsync(c.Db, 20, c.Coin.Id)); // now paid
        Assert.Equal(15, await PointsAsync(c.Db, 1, 20));          // tier D default
        Assert.Equal(MissionStatus.Closed, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task IntakeAccept_NonManager_IsRejected()
    {
        var c = await SeededAsync();
        await SetApprovalAsync(c, intake: true, FinalApprovalMode.Off);
        await FundAsync(c, 10, 100);
        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);
        var q = await c.Db.Missions.SingleAsync();

        Assert.True((await c.Board.AcceptIntakeAsync(1, q.Id.ToString(), QuestTier.C, false, 99)).IsError);
        Assert.Equal(MissionStatus.PendingApproval, (await c.Db.Missions.SingleAsync()).Status);
    }

    [Fact]
    public async Task GuildQuest_NonSpendableCurrency_IsRejected()
    {
        var c = await SeededAsync();
        var result = await c.Board.PostAsync(1, 1, QuestKind.Guild, "Patrol", "POINTS", 10);
        Assert.True(result.IsError);
        Assert.Contains("spendable", result.Message);
    }

    [Fact]
    public async Task RepeatableGuildQuest_StaysOpenAfterApproval_NonRepeatableCloses()
    {
        var c = await SeededAsync();
        await c.Board.PostAsync(1, 1, QuestKind.Guild, "Daily patrol", c.Coin.Code, 50, repeatable: true);
        var repeatable = await c.Db.Missions.SingleAsync();
        await c.Board.ClaimAsync(1, repeatable.Id.ToString(), 20);
        await c.Board.SubmitAsync(1, repeatable.Id.ToString(), 20);
        await c.Board.ApproveAsync(1, repeatable.Id.ToString(), 20, 1);
        Assert.Equal(MissionStatus.Open, (await c.Db.Missions.SingleAsync(m => m.Id == repeatable.Id)).Status);

        await c.Board.PostAsync(1, 1, QuestKind.Guild, "One off", c.Coin.Code, 50);
        var once = await c.Db.Missions.SingleAsync(m => m.Name == "One off");
        await c.Board.ClaimAsync(1, once.Id.ToString(), 21);
        await c.Board.SubmitAsync(1, once.Id.ToString(), 21);
        await c.Board.ApproveAsync(1, once.Id.ToString(), 21, 1);
        Assert.Equal(MissionStatus.Closed, (await c.Db.Missions.SingleAsync(m => m.Id == once.Id)).Status);
    }

    [Fact]
    public async Task RequestRevision_SendsBack_ThenWorkerResubmits()
    {
        var c = await SeededAsync();
        await FundAsync(c, 10, 100);
        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);
        var q = await c.Db.Missions.SingleAsync();
        await c.Board.ClaimAsync(1, q.Id.ToString(), 20);
        await c.Board.SubmitAsync(1, q.Id.ToString(), 20, "done");

        Assert.False((await c.Board.RequestRevisionAsync(1, q.Id.ToString(), 10, note: "add detail")).IsError);
        var p = await c.Db.MissionParticipants.SingleAsync(x => x.UserId == 20);
        Assert.Equal(MissionParticipantStatus.RevisionRequested, p.Status);
        Assert.Equal(1, p.RevisionCount);

        Assert.False((await c.Board.SubmitAsync(1, q.Id.ToString(), 20, "fixed")).IsError);
        Assert.Equal(MissionParticipantStatus.Submitted, (await c.Db.MissionParticipants.SingleAsync(x => x.UserId == 20)).Status);
        Assert.Equal(0, await BalanceAsync(c.Db, 20, c.Coin.Id)); // not paid until confirmed
    }

    [Fact]
    public async Task MaxActiveClaimsPerUser_BlocksSecondClaim()
    {
        var c = await SeededAsync();
        var guild = await c.Db.Guilds.SingleAsync();
        guild.Settings.MaxActiveClaimsPerUser = 1;
        await FundAsync(c, 10, 100);
        await c.Db.SaveChangesAsync();

        await c.Board.PostAsync(1, 10, QuestKind.Personal, "A", c.Coin.Code, 10);
        await c.Board.PostAsync(1, 10, QuestKind.Personal, "B", c.Coin.Code, 10);
        var a = await c.Db.Missions.SingleAsync(m => m.Name == "A");
        var b = await c.Db.Missions.SingleAsync(m => m.Name == "B");

        Assert.False((await c.Board.ClaimAsync(1, a.Id.ToString(), 20)).IsError);
        var second = await c.Board.ClaimAsync(1, b.Id.ToString(), 20);
        Assert.True(second.IsError);
        Assert.Contains("working on", second.Message);
    }

    [Fact]
    public async Task MaxOpenQuestsPerPoster_BlocksPosting()
    {
        var c = await SeededAsync();
        var guild = await c.Db.Guilds.SingleAsync();
        guild.Settings.MaxOpenQuestsPerPoster = 1;
        await FundAsync(c, 10, 100);
        await c.Db.SaveChangesAsync();

        Assert.False((await c.Board.PostAsync(1, 10, QuestKind.Personal, "A", c.Coin.Code, 10)).IsError);
        var blocked = await c.Board.PostAsync(1, 10, QuestKind.Personal, "B", c.Coin.Code, 10);
        Assert.True(blocked.IsError);
        Assert.Contains("active quest", blocked.Message);
    }

    [Fact]
    public async Task GuildQuest_Capacity_AllowsNCompletionsThenCloses()
    {
        var c = await SeededAsync();
        await c.Board.PostAsync(1, 1, QuestKind.Guild, "Patrol", c.Coin.Code, 50, capacity: 2);
        var q = await c.Db.Missions.SingleAsync();
        Assert.Equal(2, q.Capacity);

        await c.Board.ClaimAsync(1, q.Id.ToString(), 20);
        await c.Board.SubmitAsync(1, q.Id.ToString(), 20);
        await c.Board.ApproveAsync(1, q.Id.ToString(), 20, 1);
        Assert.Equal(MissionStatus.Open, (await c.Db.Missions.SingleAsync()).Status); // 1 of 2

        await c.Board.ClaimAsync(1, q.Id.ToString(), 21);
        await c.Board.SubmitAsync(1, q.Id.ToString(), 21);
        await c.Board.ApproveAsync(1, q.Id.ToString(), 21, 1);
        Assert.Equal(MissionStatus.Closed, (await c.Db.Missions.SingleAsync()).Status); // 2 of 2 → closed

        // A third claimer can't get in.
        Assert.True((await c.Board.ClaimAsync(1, q.Id.ToString(), 22)).IsError);
    }

    [Fact]
    public async Task GuildQuest_Capacity_BlocksOverfillingSlots()
    {
        var c = await SeededAsync();
        await c.Board.PostAsync(1, 1, QuestKind.Guild, "Patrol", c.Coin.Code, 50, capacity: 1);
        var q = await c.Db.Missions.SingleAsync();
        Assert.False((await c.Board.ClaimAsync(1, q.Id.ToString(), 20)).IsError);
        Assert.True((await c.Board.ClaimAsync(1, q.Id.ToString(), 21)).IsError); // full
    }

    [Fact]
    public async Task Edit_BeforeClaim_Succeeds_AfterClaim_Locked()
    {
        var c = await SeededAsync();
        await c.Board.PostAsync(1, 1, QuestKind.Guild, "Patrol", c.Coin.Code, 50);
        var q = await c.Db.Missions.SingleAsync();

        Assert.False((await c.Board.EditAsync(1, q.Id.ToString(), 1, name: "Patrol v2", reward: 80)).IsError);
        var edited = await c.Db.Missions.SingleAsync();
        Assert.Equal("Patrol v2", edited.Name);
        Assert.Equal(80, edited.RewardAmount);

        await c.Board.ClaimAsync(1, q.Id.ToString(), 20);
        var locked = await c.Board.EditAsync(1, q.Id.ToString(), 1, name: "Patrol v3");
        Assert.True(locked.IsError);
        Assert.Contains("working on it", locked.Message);
    }

    [Fact]
    public async Task RevisionCap_BlocksBeyondLimit()
    {
        var c = await SeededAsync();
        var guild = await c.Db.Guilds.SingleAsync();
        guild.Settings.MaxRevisions = 1;
        await FundAsync(c, 10, 100);
        await c.Db.SaveChangesAsync();

        await c.Board.PostAsync(1, 10, QuestKind.Personal, "Escort", c.Coin.Code, 40);
        var q = await c.Db.Missions.SingleAsync();
        await c.Board.ClaimAsync(1, q.Id.ToString(), 20);
        await c.Board.SubmitAsync(1, q.Id.ToString(), 20);

        Assert.False((await c.Board.RequestRevisionAsync(1, q.Id.ToString(), 10, note: "fix")).IsError); // 1st
        await c.Board.SubmitAsync(1, q.Id.ToString(), 20);
        var second = await c.Board.RequestRevisionAsync(1, q.Id.ToString(), 10, note: "again");
        Assert.True(second.IsError); // exceeded cap of 1
    }
}
