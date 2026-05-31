using Muster.Contracts;
using Muster.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Musters;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Commands.Tracking;
using Muster.Persistence.Queries;

namespace Muster.IntegrationTests;

public class ParticipationTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<(MusterDbContext db, Currency points)> SeededAsync(ulong guildId = 1)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(guildId, "G", null);
        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        return (db, points);
    }

    private static IReadOnlyDictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> Occupant(ulong channelId, params ulong[] userIds)
        => new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>>
        {
            [channelId] = userIds.Select(u => new VoiceMemberSnapshot(u, IsBot: false, IsMuted: false, IsDeafened: false)).ToList(),
        };

    private static readonly IReadOnlyDictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> NoOne = new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>>();

    /// <summary>Count raw presence (no anti-AFK guards) so single-user accrual maths are exact.</summary>
    private static async Task DisableSessionGuardsAsync(MusterDbContext db)
    {
        var g = await db.Guilds.FirstAsync();
        g.Settings.ApplyAfkGuardsToSessions = false;
        g.Settings = g.Settings;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Award_IsIdempotent_OnSameSource()
    {
        var (db, points) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());

        await awards.AwardAsync(1, 10, points.Id, 50, CurrencyLedgerSource.ManualAward, "src-1", "r");
        await awards.AwardAsync(1, 10, points.Id, 50, CurrencyLedgerSource.ManualAward, "src-1", "r"); // dup

        Assert.Equal(1, await db.CurrencyLedgerEntries.CountAsync());
        var wallet = await db.Wallets.SingleAsync();
        Assert.Equal(50, wallet.Balance);
    }

    [Fact]
    public async Task Muster_CheckIn_RespectsCapacity_AndPaysRosterOnClose()
    {
        var (db, points) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        var musters = new MusterService(db, awards, new GuildAuthorizationService(db));

        var muster = await musters.CreateAsync(1, 100, null, "Roll call", points.Id, 10, capacity: 1, expiresAt: null, createdBy: 5);

        Assert.Equal(ReactionOutcome.Recorded, await musters.CheckInAsync(muster.Id, 10, MusterParticipantSource.Button));
        Assert.Equal(ReactionOutcome.AlreadyParticipated, await musters.CheckInAsync(muster.Id, 10, MusterParticipantSource.Button));
        Assert.Equal(ReactionOutcome.Full, await musters.CheckInAsync(muster.Id, 20, MusterParticipantSource.Button)); // capacity = 1

        // Reward is paid at close, not on check-in.
        Assert.Equal(0, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Muster));

        Assert.True(await musters.CloseAsync(muster.Id));
        Assert.Equal(1, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Muster));
        Assert.Equal(10, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);

        // Re-closing is idempotent — no second payout.
        Assert.False(await musters.CloseAsync(muster.Id));
        Assert.Equal(1, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Muster));
    }

    private static async Task<Currency> AddSessionCoinAsync(MusterDbContext db, int minutesPerCoin = 1)
    {
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        var guild = await db.Guilds.FirstAsync();
        guild.Settings.SessionCoinCurrencyCode = "COIN";
        guild.Settings.MinutesPerCoin = minutesPerCoin;
        guild.Settings = guild.Settings;
        await db.SaveChangesAsync();
        return coin;
    }

    private static Task<int> CoinLedgerCountAsync(MusterDbContext db, Guid coinId, ulong userId)
        => db.CurrencyLedgerEntries.CountAsync(e => e.CurrencyId == coinId && e.UserId == userId);

    [Fact]
    public async Task LinkedMuster_GatesSessionCoin_ToCheckedInAttendeesOnly()
    {
        var (db, points) = await SeededAsync();
        await DisableSessionGuardsAsync(db);
        var coin = await AddSessionCoinAsync(db);

        var awards = new CurrencyService(db, new RecordingMessageBus());
        var musters = new MusterService(db, awards, new GuildAuthorizationService(db));
        var sessions = new TrackingSessionService(db, awards, new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());

        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        // A muster linked to the session, gating coin on check-in. Only user 10 checks in.
        var muster = await musters.CreateAsync(1, 100, null, "Roll call", points.Id, 0, capacity: null, expiresAt: null, createdBy: 5, sessionId: session.Id);
        Assert.Equal(ReactionOutcome.Recorded, await musters.CheckInAsync(muster.Id, 10, MusterParticipantSource.Button));
        session.CoinGate = SessionCoinGate.Any;
        await db.SaveChangesAsync();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10, 20), t0); // both in voice
        await sessions.CloseAsync(session.Id, at: t0.AddMinutes(30), pointsPerMinute: 1);

        // Both earned POINTS for voice time; only the checked-in member (10) earned the gated COIN.
        Assert.True((await db.Wallets.SingleAsync(w => w.UserId == 10 && w.CurrencyId == points.Id)).Balance > 0);
        Assert.True((await db.Wallets.SingleAsync(w => w.UserId == 20 && w.CurrencyId == points.Id)).Balance > 0);
        Assert.Equal(1, await CoinLedgerCountAsync(db, coin.Id, 10));
        Assert.Equal(0, await CoinLedgerCountAsync(db, coin.Id, 20)); // not checked in → no coin
    }

    [Fact]
    public async Task AllGate_RequiresEveryAssignedMuster()
    {
        var (db, points) = await SeededAsync();
        await DisableSessionGuardsAsync(db);
        var coin = await AddSessionCoinAsync(db);

        var awards = new CurrencyService(db, new RecordingMessageBus());
        var musters = new MusterService(db, awards, new GuildAuthorizationService(db));
        var sessions = new TrackingSessionService(db, awards, new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());

        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        // Two linked musters under an All gate. User 20 checks into both; user 10 only into round 1.
        var round1 = await musters.CreateAsync(1, 100, null, "Round 1", points.Id, 0, capacity: null, expiresAt: null, createdBy: 5, sessionId: session.Id);
        var round2 = await musters.CreateAsync(1, 100, null, "Round 2", points.Id, 0, capacity: null, expiresAt: null, createdBy: 5, sessionId: session.Id);
        await musters.CheckInAsync(round1.Id, 10, MusterParticipantSource.Button);
        await musters.CheckInAsync(round1.Id, 20, MusterParticipantSource.Button);
        await musters.CheckInAsync(round2.Id, 20, MusterParticipantSource.Button);
        session.CoinGate = SessionCoinGate.All;
        await db.SaveChangesAsync();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10, 20), t0); // both attend
        await sessions.CloseAsync(session.Id, at: t0.AddMinutes(30), pointsPerMinute: 1);

        // All gate: only the member in EVERY linked muster (20) earns the coin; 10 missed round 2.
        Assert.Equal(1, await CoinLedgerCountAsync(db, coin.Id, 20));
        Assert.Equal(0, await CoinLedgerCountAsync(db, coin.Id, 10));
    }

    [Fact]
    public async Task NoLinkedMuster_MintsCoinToAllEligibleAttendees()
    {
        var (db, _) = await SeededAsync();
        await DisableSessionGuardsAsync(db);
        var coin = await AddSessionCoinAsync(db);

        var awards = new CurrencyService(db, new RecordingMessageBus());
        var sessions = new TrackingSessionService(db, awards, new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());

        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5); // CoinGate defaults to None

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10, 20), t0);
        await sessions.CloseAsync(session.Id, at: t0.AddMinutes(30), pointsPerMinute: 1);

        // No muster gate → everyone who attended earns the coin.
        Assert.Equal(1, await CoinLedgerCountAsync(db, coin.Id, 10));
        Assert.Equal(1, await CoinLedgerCountAsync(db, coin.Id, 20));
    }

    [Fact]
    public async Task Muster_CancelledClose_PaysNothing()
    {
        var (db, points) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        var musters = new MusterService(db, awards, new GuildAuthorizationService(db));

        var muster = await musters.CreateAsync(1, 100, null, "Void", points.Id, 10, capacity: null, expiresAt: null, createdBy: 5);
        await musters.CheckInAsync(muster.Id, 10, MusterParticipantSource.Button);

        Assert.True(await musters.CloseAsync(muster.Id, MusterStatus.Cancelled));
        Assert.Equal(0, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Muster));
    }

    [Fact]
    public async Task LinkedMuster_BonusDeferredToClose_PaidOnlyToCheckedInAttendees()
    {
        var (db, points) = await SeededAsync();
        await DisableSessionGuardsAsync(db);

        var awards = new CurrencyService(db, new RecordingMessageBus());
        var musters = new MusterService(db, awards, new GuildAuthorizationService(db));
        var sessions = new TrackingSessionService(db, awards, new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());

        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);
        var muster = await musters.CreateAsync(1, 100, null, "Bonus round", points.Id, 50, capacity: null, expiresAt: null, createdBy: 5, sessionId: session.Id);

        // 10 checks in and attends; 30 checks in but never shows up; 20 attends but never checks in.
        await musters.CheckInAsync(muster.Id, 10, MusterParticipantSource.Button);
        await musters.CheckInAsync(muster.Id, 30, MusterParticipantSource.Button);

        // Linked → the bonus is NOT paid at check-in.
        Assert.Equal(0, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Muster));

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10, 20), t0);
        await sessions.CloseAsync(session.Id, at: t0.AddMinutes(30), pointsPerMinute: 1);

        // Bonus paid at close only to 10 (checked in AND attended). 30 (no show) and 20 (no check-in) get none.
        Assert.Equal(1, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Muster && e.UserId == 10));
        Assert.Equal(0, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Muster && e.UserId == 30));
        Assert.Equal(0, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Muster && e.UserId == 20));

        // 10's POINTS = 30 voice min × 1 + 50 muster bonus = 80.
        Assert.Equal(80, (await db.Wallets.SingleAsync(w => w.UserId == 10 && w.CurrencyId == points.Id)).Balance);
    }

    [Fact]
    public async Task AutoCreateMuster_OnSessionOpen_LinksMusterAndGatesCoin()
    {
        var (db, _) = await SeededAsync();
        var guild = await db.Guilds.FirstAsync();
        guild.Settings.AutoCreateMusterOnSession = true;
        guild.Settings = guild.Settings;
        await db.SaveChangesAsync();

        var awards = new CurrencyService(db, new RecordingMessageBus());
        var auth = new GuildAuthorizationService(db);
        var musters = new MusterService(db, awards, auth);
        var sessions = new TrackingSessionService(db, awards, auth, new RewardMultiplierService(db), new RecordingMessageBus(), musters);

        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var muster = await db.ReactionMusters.SingleAsync();
        Assert.Equal(1, await db.MusterSessionLinks.CountAsync(l => l.SessionId == session.Id && l.MusterId == muster.Id));
        Assert.Equal(SessionCoinGate.Any, (await db.TrackingSessions.SingleAsync(s => s.Id == session.Id)).CoinGate);

        // Per-session override beats the guild default.
        var noMuster = await sessions.OpenManualAsync(1, voiceChannelId: 600, openedBy: 5, createMuster: false);
        Assert.Equal(0, await db.MusterSessionLinks.CountAsync(l => l.SessionId == noMuster.Id));
        Assert.Equal(SessionCoinGate.None, (await db.TrackingSessions.SingleAsync(s => s.Id == noMuster.Id)).CoinGate);
    }

    [Fact]
    public async Task SessionClose_AutoClosesLinkedMuster()
    {
        var (db, points) = await SeededAsync();
        await DisableSessionGuardsAsync(db);

        var awards = new CurrencyService(db, new RecordingMessageBus());
        var musters = new MusterService(db, awards, new GuildAuthorizationService(db));
        var sessions = new TrackingSessionService(db, awards, new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());

        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);
        var muster = await musters.CreateAsync(1, 100, null, "Roll call", points.Id, 0, capacity: null, expiresAt: null, createdBy: 5, sessionId: session.Id);

        await sessions.CloseAsync(session.Id, at: DateTimeOffset.UtcNow);

        var closed = await db.ReactionMusters.SingleAsync(m => m.Id == muster.Id);
        Assert.Equal(MusterStatus.Closed, closed.Status);
        Assert.NotNull(closed.ClosedAt);
    }

    [Fact]
    public async Task Quest_AwardsOnApproval_AndNotTwice()
    {
        var (db, points) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        var quests = new QuestService(db, awards, new GuildAuthorizationService(db), new RecordingMessageBus());

        var quest = (await quests.PostQuestAsync(new QuestDraft(1, 5, QuestOrigin.Guild, "Recruit", "Bring a friend", points.Id, 100))).Quest!;
        await quests.ClaimAsync(quest.Id, 10);
        await quests.SubmitAsync(quest.Id, 10);
        await quests.ApproveAsync(quest.Id, 10, reviewerId: 5);
        await quests.ApproveAsync(quest.Id, 10, reviewerId: 5); // idempotent

        Assert.Equal(100, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
        Assert.Equal(1, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Quest));
    }

    [Fact]
    public async Task GuildQuest_OwnerCanParticipate_AndOncePerMemberByDefault()
    {
        var (db, points) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        var quests = new QuestService(db, awards, new GuildAuthorizationService(db), new RecordingMessageBus());

        // Capacity 2 keeps the quest Open after one completion, so the re-claim hits the one-per-member guard
        // (not a closed-quest refusal).
        var quest = (await quests.PostQuestAsync(new QuestDraft(1, 5, QuestOrigin.Guild, "Patrol", "", points.Id, 10, Capacity: 2))).Quest!;

        // The guild quest's creator may participate (the guild owns it, not the poster).
        Assert.Equal(QuestResult.Ok, await quests.ClaimAsync(quest.Id, 5));
        Assert.Equal(QuestResult.Ok, await quests.SubmitAsync(quest.Id, 5));
        Assert.Equal(QuestResult.Ok, await quests.ApproveAsync(quest.Id, 5, reviewerId: 5));

        // A second attempt by the same member is refused — one completion per member.
        Assert.Equal(QuestResult.AlreadyParticipated, await quests.ClaimAsync(quest.Id, 5));
        Assert.Equal(QuestResult.InvalidState, await quests.SubmitAsync(quest.Id, 5)); // nothing new to submit
        Assert.Equal(10, await PointsLedgerAsync(db, points.Id, 5)); // paid once
    }

    private static async Task<long> PointsLedgerAsync(MusterDbContext db, Guid pointsId, ulong userId)
        => await db.CurrencyLedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == pointsId).SumAsync(e => (long?)e.Amount) ?? 0;

    [Fact]
    public async Task Session_GuardsOn_PausesDeafenedAndCreditsPeer()
    {
        var (db, _) = await SeededAsync(); // ApplyAfkGuardsToSessions defaults true → undeafened + not-alone guards on
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var roster = new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>>
        {
            [500] = new[] { new VoiceMemberSnapshot(10, false, IsMuted: false, IsDeafened: true), new VoiceMemberSnapshot(20, false, false, false) },
        };
        var t0 = DateTimeOffset.UtcNow;
        await sessions.ReconcileSessionsAsync(1, roster, t0);
        await sessions.ReconcileSessionsAsync(1, roster, t0.AddMinutes(10));

        Assert.Equal(0, (await db.VoiceAttendance.SingleAsync(a => a.UserId == 10)).TotalMinutes);  // deafened: paused
        Assert.Equal(10, (await db.VoiceAttendance.SingleAsync(a => a.UserId == 20)).TotalMinutes); // listening peer
    }

    [Fact]
    public async Task Session_GuardsOn_AllowsMutedButPresent()
    {
        var (db, _) = await SeededAsync(); // guards on, but muted is allowed by default (phone-call case)
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());
        await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var roster = new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>>
        {
            [500] = new[] { new VoiceMemberSnapshot(10, false, IsMuted: true, IsDeafened: false), new VoiceMemberSnapshot(20, false, false, false) },
        };
        var t0 = DateTimeOffset.UtcNow;
        await sessions.ReconcileSessionsAsync(1, roster, t0);
        await sessions.ReconcileSessionsAsync(1, roster, t0.AddMinutes(10));

        Assert.Equal(10, (await db.VoiceAttendance.SingleAsync(a => a.UserId == 10)).TotalMinutes);  // muted but listening → still earns
    }

    [Fact]
    public async Task Session_AllOutMember_NotTracked()
    {
        var (db, _) = await SeededAsync();
        db.GuildMembers.Add(new GuildMember { GuildId = 1, UserId = 10, Tracking = TrackingChoice.AllOut, JoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());
        await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var roster = new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> { [500] = new[] { new VoiceMemberSnapshot(10, false, false, false) } };
        await sessions.ReconcileSessionsAsync(1, roster, DateTimeOffset.UtcNow);

        Assert.Empty(await db.VoiceAttendance.ToListAsync()); // opted out of all tracking → no attendance row
    }

    [Fact]
    public async Task OptOutAll_EvictsActiveAttendanceAndBackground()
    {
        var (db, _) = await SeededAsync();
        var sid = Guid.NewGuid();
        db.TrackingSessions.Add(new TrackingSession { Id = sid, GuildId = 1, Name = "Op", Source = TrackingSessionSource.Manual, VoiceChannelId = 500, StartedAt = DateTimeOffset.UtcNow, Status = TrackingSessionStatus.Active });
        db.VoiceAttendance.Add(new VoiceAttendance { Id = Guid.NewGuid(), TrackingSessionId = sid, UserId = 10, FirstJoinedAt = DateTimeOffset.UtcNow, TotalMinutes = 5 });
        db.BackgroundVoicePresences.Add(new BackgroundVoicePresence { Id = Guid.NewGuid(), GuildId = 1, UserId = 10, ChannelId = 600 });
        await db.SaveChangesAsync();

        var result = await new TrackingPreferenceCommandService(db, new AlwaysAllowTrackingAuthorizer())
            .SetAsync(1, actorId: 10, userId: 10, TrackingChoice.AllOut);

        Assert.False(result.IsError);
        Assert.Empty(await db.VoiceAttendance.ToListAsync());
        Assert.Empty(await db.BackgroundVoicePresences.ToListAsync());
    }

    [Fact]
    public async Task OptOutOfSession_RemovesAttendance_AndExcludesFromReconcile()
    {
        var (db, _) = await SeededAsync();
        await DisableSessionGuardsAsync(db);
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);
        var t0 = DateTimeOffset.UtcNow;

        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10), t0);                 // user 10 accrues
        var opted = await sessions.OptOutMemberAsync(1, session.Id, 10);                 // opts out of this session
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10), t0.AddMinutes(10));  // still present, but excluded

        Assert.True(opted);
        Assert.Empty(await db.VoiceAttendance.ToListAsync()); // removed on opt-out and not re-created
    }

    [Fact]
    public async Task MinTrackedSeconds_DropsDriveBy_KeepsLongerAttendee()
    {
        var (db, _) = await SeededAsync();
        await DisableSessionGuardsAsync(db);
        var guild = await db.Guilds.FirstAsync();
        guild.Settings.MinTrackedSeconds = 60;
        guild.Settings = guild.Settings;
        await db.SaveChangesAsync();

        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());
        await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);
        var t0 = DateTimeOffset.UtcNow;

        // Both join; user 10 leaves after 20s (drive-by), user 20 leaves after 5 min.
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10, 20), t0);
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 20), t0.AddSeconds(20));   // 10 left
        await sessions.ReconcileSessionsAsync(1, NoOne, t0.AddMinutes(5));                // 20 left

        var rows = await db.VoiceAttendance.ToListAsync();
        var row = Assert.Single(rows);          // the drive-by row was dropped
        Assert.Equal(20ul, row.UserId);
    }

    [Fact]
    public async Task SessionFlush_ClampsAbsurdGap()
    {
        var (db, _) = await SeededAsync();
        await DisableSessionGuardsAsync(db);
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var t0 = DateTimeOffset.UtcNow;
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10), t0);     // opens segment
        await sessions.CloseAsync(session.Id, at: t0.AddHours(13));          // 13h gap, no intervening flush

        // Clamped to the 12h (720 min) sanity bound, not 780.
        Assert.Equal(720, (await db.VoiceAttendance.SingleAsync(a => a.UserId == 10)).TotalMinutes);
    }

    [Fact]
    public async Task CloseStaleSessions_ClosesPastMaxHours()
    {
        var (db, _) = await SeededAsync(); // MaxSessionHours defaults to 24
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);
        session.StartedAt = DateTimeOffset.UtcNow.AddHours(-25);
        await db.SaveChangesAsync();

        var closed = await sessions.CloseStaleSessionsAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, closed);
        Assert.Equal(TrackingSessionStatus.Closed, (await db.TrackingSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Session_GuardsOn_AloneMember_NotAccrued()
    {
        var (db, _) = await SeededAsync();
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var roster = new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> { [500] = new[] { new VoiceMemberSnapshot(10, false, false, false) } };
        var t0 = DateTimeOffset.UtcNow;
        await sessions.ReconcileSessionsAsync(1, roster, t0);
        await sessions.ReconcileSessionsAsync(1, roster, t0.AddMinutes(10));

        Assert.Equal(0, (await db.VoiceAttendance.SingleAsync(a => a.UserId == 10)).TotalMinutes); // alone: paused
    }

    [Fact]
    public async Task TrackingSession_AwardsByVoiceMinutes_OnClose()
    {
        var (db, _) = await SeededAsync();
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());

        await DisableSessionGuardsAsync(db);
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var joinedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10), joinedAt);
        await sessions.CloseAsync(session.Id, at: joinedAt.AddMinutes(30), pointsPerMinute: 2);

        var attendance = await db.VoiceAttendance.SingleAsync();
        Assert.Equal(30, attendance.TotalMinutes);
        Assert.Equal(60, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance); // 30 min * 2
    }

    [Fact]
    public async Task TrackingSession_ClosingSegment_OnChannelLeave()
    {
        var (db, _) = await SeededAsync();
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db), new RecordingMessageBus());
        await DisableSessionGuardsAsync(db);
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await sessions.ReconcileSessionsAsync(1, Occupant(500, 10), t0);              // join
        await sessions.ReconcileSessionsAsync(1, NoOne, t0.AddMinutes(15));           // leave

        var attendance = await db.VoiceAttendance.SingleAsync();
        Assert.Null(attendance.OpenSegmentStart);
        Assert.Equal(15, attendance.TotalMinutes);
    }
}
