using Muster.Contracts;
using Muster.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Entities.Musters;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Messaging;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Musters;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Commands.Quests;

namespace Muster.IntegrationTests;

public class MusterQuestCommandTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync(ulong guildId = 1, ulong ownerId = 0)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(guildId, "G", null, ownerId: ownerId);
        return db;
    }

    private static (MusterService musters, GuildAuthorizationService auth, GuildMusterSettingsService store, RecordingMessageBus bus) NewMusters(MusterDbContext db)
    {
        var auth = new GuildAuthorizationService(db);
        var store = new GuildMusterSettingsService(db, Options.Create(new GuildMusterSettings()));
        return (new MusterService(db, new CurrencyService(db, new RecordingMessageBus()), auth), auth, store, new RecordingMessageBus());
    }

    [Fact]
    public async Task Muster_Create_StoresMuster_ViaFunnel()
    {
        using var db = await SeededAsync(ownerId: 7); // owner = admin → passes the TrackingManager gate
        var (musters, auth, store, bus) = NewMusters(db);

        var result = await CreateMusterHandler.Handle(
            new CreateMuster(1, ActorId: 7, ChannelId: 500, Title: null, Prompt: "Roll call",
                TemplateId: null, Points: 10, Coins: null, CoinCurrencyId: null, Capacity: null, ExpiresAt: null, SessionId: null),
            auth, db, musters, store, bus, default);

        Assert.True(result.Ok);

        var muster = await db.ReactionMusters.SingleAsync();
        Assert.Equal(result.Value, muster.Id);
        Assert.Equal(500ul, muster.ChannelId);
        Assert.Equal(10, muster.Points);
        Assert.Equal(7ul, muster.CreatedBy);
    }

    [Fact]
    public async Task Muster_Create_RejectsBadInput_AndNonManagers()
    {
        using var db = await SeededAsync(ownerId: 7);
        var (musters, auth, store, bus) = NewMusters(db);

        CreateMuster Cmd(ulong actor, string prompt, long points, int? capacity) =>
            new(1, actor, 500, null, prompt, TemplateId: null, Points: points, Coins: null, CoinCurrencyId: null, Capacity: capacity, ExpiresAt: null, SessionId: null);

        Assert.Equal("PromptRequired", (await CreateMusterHandler.Handle(Cmd(7, "", 10, null), auth, db, musters, store, bus, default)).Status);
        Assert.Equal("RewardNegative", (await CreateMusterHandler.Handle(Cmd(7, "p", -1, null), auth, db, musters, store, bus, default)).Status);
        Assert.Equal("BadCapacity", (await CreateMusterHandler.Handle(Cmd(7, "p", 10, 0), auth, db, musters, store, bus, default)).Status);

        // A non-manager with otherwise-valid input is refused at the gate.
        Assert.Equal("Forbidden", (await CreateMusterHandler.Handle(Cmd(999, "p", 10, null), auth, db, musters, store, bus, default)).Status);

        // Coins with a currency that doesn't belong to the guild are rejected.
        var cross = new CreateMuster(1, 7, 500, null, "p", TemplateId: null, Points: 0, Coins: 5, CoinCurrencyId: Guid.NewGuid(), Capacity: null, ExpiresAt: null, SessionId: null);
        Assert.Equal("CoinCurrencyInvalid", (await CreateMusterHandler.Handle(cross, auth, db, musters, store, bus, default)).Status);

        Assert.Equal(0, await db.ReactionMusters.CountAsync());
    }

    [Fact]
    public async Task Muster_Create_Templates_CreatorGating_AndManagerOverride()
    {
        using var db = await SeededAsync(ownerId: 7); // 7 = owner/manager
        var (musters, auth, store, bus) = NewMusters(db);

        var coinId = Guid.NewGuid();
        db.Currencies.Add(new Currency { Id = coinId, GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true });
        var tplId = Guid.NewGuid();
        db.MusterTemplates.Add(new MusterTemplate { Id = tplId, GuildId = 1, Name = "Strike", Points = 5, Coins = 3, CoinCurrencyId = coinId, RetentionHours = 24, Enabled = true });
        var guild = await db.Guilds.SingleAsync();
        guild.Settings.MusterCreatorRoleIds = [500];
        await db.SaveChangesAsync();
        await new MemberSyncService(db).UpsertAsync(1, 50, "creator", null, null, roleIds: [500]); // template-only creator

        CreateMuster Cmd(ulong actor, Guid? template, long? points) =>
            new(1, actor, 500, null, "go", template, points, null, null, null, null, null);

        // Creator must use a template.
        Assert.Equal("TemplateRequired", (await CreateMusterHandler.Handle(Cmd(50, null, null), auth, db, musters, store, bus, default)).Status);

        // Creator with a template → template values applied; no override allowed.
        Assert.True((await CreateMusterHandler.Handle(Cmd(50, tplId, 999), auth, db, musters, store, bus, default)).Ok);
        var byCreator = await db.ReactionMusters.OrderByDescending(m => m.CreatedAt).FirstAsync();
        Assert.Equal(5, byCreator.Points);   // creator's points override is ignored
        Assert.Equal(3, byCreator.Coins);
        Assert.Equal(coinId, byCreator.CoinCurrencyId);
        Assert.Equal(24, byCreator.RetentionHours);

        // Manager with the same template may override points; coins stay from the template.
        Assert.True((await CreateMusterHandler.Handle(Cmd(7, tplId, 20), auth, db, musters, store, bus, default)).Ok);
        var byManager = await db.ReactionMusters.OrderByDescending(m => m.CreatedAt).FirstAsync();
        Assert.Equal(20, byManager.Points);
        Assert.Equal(3, byManager.Coins);
    }

    [Fact]
    public async Task Muster_Create_Template_FillsTitleAndPrompt_AndBothRewards()
    {
        using var db = await SeededAsync(ownerId: 7);
        var (musters, auth, store, bus) = NewMusters(db);

        var coinId = Guid.NewGuid();
        db.Currencies.Add(new Currency { Id = coinId, GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true });
        var tplId = Guid.NewGuid();
        db.MusterTemplates.Add(new MusterTemplate
        {
            Id = tplId, GuildId = 1, Name = "Strike", Enabled = true,
            Title = "Strike Group", Prompt = "Check in for the strike",
            Points = 5, Coins = 3, CoinCurrencyId = coinId,
        });
        await db.SaveChangesAsync();

        // Author leaves title + prompt blank; the template supplies both, and points + coins both apply together.
        var cmd = new CreateMuster(1, 7, 500, Title: null, Prompt: "", TemplateId: tplId,
            Points: null, Coins: null, CoinCurrencyId: null, Capacity: null, ExpiresAt: null, SessionId: null);
        Assert.True((await CreateMusterHandler.Handle(cmd, auth, db, musters, store, bus, default)).Ok);

        var m = await db.ReactionMusters.OrderByDescending(x => x.CreatedAt).FirstAsync();
        Assert.Equal("Strike Group", m.Title);
        Assert.Equal("Check in for the strike", m.Prompt);
        Assert.Equal(5, m.Points);
        Assert.Equal(3, m.Coins);
        Assert.Equal(coinId, m.CoinCurrencyId);
    }

    [Fact]
    public async Task Muster_Create_NoPromptAndNoTemplatePrompt_IsRejected()
    {
        using var db = await SeededAsync(ownerId: 7);
        var (musters, auth, store, bus) = NewMusters(db);

        var cmd = new CreateMuster(1, 7, 500, Title: null, Prompt: "  ", TemplateId: null,
            Points: 1, Coins: null, CoinCurrencyId: null, Capacity: null, ExpiresAt: null, SessionId: null);
        Assert.Equal("PromptRequired", (await CreateMusterHandler.Handle(cmd, auth, db, musters, store, bus, default)).Status);
    }

    [Fact]
    public async Task Muster_Create_HonorsChannelAllowList()
    {
        using var db = await SeededAsync(ownerId: 7);
        var (musters, auth, store, bus) = NewMusters(db);

        db.GuildMusterSettings.Add(new GuildMusterSettings { GuildId = 1, AllowedChannelIds = [999] });
        await db.SaveChangesAsync();

        CreateMuster At(ulong channelId) => new(1, 7, channelId, null, "Roll call", TemplateId: null, Points: 0, Coins: null, CoinCurrencyId: null, Capacity: null, ExpiresAt: null, SessionId: null);

        // Disallowed explicit channel is rejected; an allowed one and the "fall back to default" (0) pass.
        Assert.Equal("ChannelNotAllowed", (await CreateMusterHandler.Handle(At(500), auth, db, musters, store, bus, default)).Status);
        Assert.True((await CreateMusterHandler.Handle(At(999), auth, db, musters, store, bus, default)).Ok);
        Assert.True((await CreateMusterHandler.Handle(At(0), auth, db, musters, store, bus, default)).Ok);
    }

    [Fact]
    public async Task Muster_Create_AutoChecksInCreator_WhenSet()
    {
        using var db = await SeededAsync(ownerId: 7);
        var (musters, auth, store, bus) = NewMusters(db); // default settings → CreatorAutoCheckIn = true

        CreateMuster Cmd(bool? checkIn) =>
            new(1, 7, 500, null, "Roll call", TemplateId: null, Points: 0, Coins: null, CoinCurrencyId: null,
                Capacity: null, ExpiresAt: null, SessionId: null, CheckInCreator: checkIn);

        // Default (null) → guild default (true): creator on the roster.
        Assert.True((await CreateMusterHandler.Handle(Cmd(null), auth, db, musters, store, bus, default)).Ok);
        var auto = await db.ReactionMusters.Include(m => m.Participants).OrderByDescending(m => m.CreatedAt).FirstAsync();
        Assert.Contains(auto.Participants, p => p.UserId == 7);

        // Explicit false → empty roster (creating for others).
        Assert.True((await CreateMusterHandler.Handle(Cmd(false), auth, db, musters, store, bus, default)).Ok);
        var manual = await db.ReactionMusters.Include(m => m.Participants).OrderByDescending(m => m.CreatedAt).FirstAsync();
        Assert.Empty(manual.Participants);
    }

    [Fact]
    public async Task Muster_LinkedPastExpiry_SoftCloses_NoPay_AndStaysNonTerminal()
    {
        using var db = await SeededAsync(ownerId: 7);
        var (musters, _, _, _) = NewMusters(db);

        var sessionId = Guid.NewGuid();
        db.TrackingSessions.Add(new TrackingSession { Id = sessionId, GuildId = 1, Name = "S", Status = TrackingSessionStatus.Active });
        await db.SaveChangesAsync();

        // A linked muster already past its window, with a points reward.
        var muster = await musters.CreateAsync(1, 0, null, "Check in", points: 10, coins: 0, coinCurrencyId: null,
            retentionHours: 48, capacity: null, expiresAt: DateTimeOffset.UtcNow.AddHours(-1), createdBy: 7,
            sessionId: sessionId);

        // A check-in attempt trips the lazy transition: linked → Locked (soft-closed), not Expired/Closed.
        var outcome = await musters.CheckInAsync(muster.Id, 50, MusterParticipantSource.Button);
        Assert.Equal(ReactionOutcome.Closed, outcome);

        var reloaded = await db.ReactionMusters.Include(m => m.Participants).SingleAsync(m => m.Id == muster.Id);
        Assert.Equal(MusterStatus.Locked, reloaded.Status);
        Assert.Null(reloaded.ClosedAt);              // not terminal yet
        Assert.Empty(reloaded.Participants);          // the blocked check-in didn't land

        // Linked muster pays at session close, not on lock — nobody was paid here.
        Assert.False(await db.Wallets.AnyAsync(w => w.UserId == 7 && w.Balance != 0));

        // A Locked muster can still go terminal (manager close / session close).
        Assert.True(await musters.CloseAsync(muster.Id, MusterStatus.Closed));
    }

    [Fact]
    public async Task Quest_PostClaimSubmitApprove_AwardsMember()
    {
        using var db = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        var quests = new QuestService(db, awards, new GuildAuthorizationService(db), new RecordingMessageBus());

        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var quest = (await quests.PostQuestAsync(new QuestDraft(1, 5, QuestOrigin.Guild, "Recruit", "Bring a friend", points.Id, 100))).Quest!;

        Assert.Equal(QuestResult.Ok, await quests.ClaimAsync(quest.Id, 10));
        Assert.Equal(QuestResult.Ok, await quests.SubmitAsync(quest.Id, 10));
        Assert.Equal(QuestResult.Ok, await quests.ApproveAsync(quest.Id, 10, reviewerId: 5));

        Assert.Equal(100, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
    }

    [Fact]
    public async Task Quest_Claim_InvalidId_ReturnsError()
    {
        using var db = await SeededAsync();
        var board = NewBoard(db);

        Assert.True((await board.ClaimAsync(1, "nope", 10)).IsError);
        Assert.True((await board.ClaimAsync(1, Guid.NewGuid().ToString(), 10)).IsError); // unknown quest
    }

    [Fact]
    public async Task Quest_List_FormatsOpenQuests()
    {
        using var db = await SeededAsync(ownerId: 1);
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true });
        await db.SaveChangesAsync();
        var board = NewBoard(db);

        Assert.Contains("No open quests", (await board.ListAsync(1)).Message);

        await board.PostAsync(1, actorId: 1, QuestOrigin.Guild, "Recruit", "COIN", 50, description: "Bring a friend");
        var listed = await board.ListAsync(1);
        Assert.Contains("Recruit", listed.Message);
        Assert.Contains("Guild", listed.Message);
    }

    private static QuestCommandHarness NewBoard(MusterDbContext db)
    {
        var awards = new CurrencyService(db, new RecordingMessageBus());
        var auth = new GuildAuthorizationService(db);
        var quests = new QuestService(db, awards, auth, new RecordingMessageBus());
        return new QuestCommandHarness(db, auth, quests, new QuestReadService(db));
    }
}
