using Microsoft.EntityFrameworkCore;
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
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var awards = new AwardService(db);
        var auth = new GuildAuthorizationService(db);
        var missions = new MissionService(db, awards, auth);
        var bounties = new BountyService(db, new EscrowService(db, awards), auth);
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
}
