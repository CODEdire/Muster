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
            [channelId] = userIds.Select(u => new VoiceMemberSnapshot(u, IsBot: false, IsMutedOrDeafened: false)).ToList(),
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
    public async Task Muster_Reaction_RewardsOnce_AndRespectsCapacity()
    {
        var (db, points) = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        var musters = new MusterService(db, awards, new GuildAuthorizationService(db));

        var muster = await musters.CreateAsync(1, 100, 999, "Roll call", ["✅"], points.Id, 10, capacity: 1, expiresAt: null);

        Assert.Equal(ReactionOutcome.Recorded, await musters.RecordReactionAsync(999, 10, "✅"));
        Assert.Equal(ReactionOutcome.AlreadyParticipated, await musters.RecordReactionAsync(999, 10, "✅"));
        Assert.Equal(ReactionOutcome.Full, await musters.RecordReactionAsync(999, 20, "✅")); // capacity = 1

        Assert.Equal(1, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Muster));
        Assert.Equal(10, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
        _ = muster;
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
    public async Task Session_GuardsOn_PausesMutedAndCreditsUnmutedPeer()
    {
        var (db, _) = await SeededAsync(); // ApplyAfkGuardsToSessions defaults true
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var roster = new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>>
        {
            [500] = new[] { new VoiceMemberSnapshot(10, false, IsMutedOrDeafened: true), new VoiceMemberSnapshot(20, false, false) },
        };
        var t0 = DateTimeOffset.UtcNow;
        await sessions.ReconcileSessionsAsync(1, roster, t0);
        await sessions.ReconcileSessionsAsync(1, roster, t0.AddMinutes(10));

        Assert.Equal(0, (await db.VoiceAttendance.SingleAsync(a => a.UserId == 10)).TotalMinutes);  // muted: paused
        Assert.Equal(10, (await db.VoiceAttendance.SingleAsync(a => a.UserId == 20)).TotalMinutes); // unmuted peer
    }

    [Fact]
    public async Task Session_AllOutMember_NotTracked()
    {
        var (db, _) = await SeededAsync();
        db.GuildMembers.Add(new GuildMember { GuildId = 1, UserId = 10, Tracking = TrackingChoice.AllOut, JoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));
        await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var roster = new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> { [500] = new[] { new VoiceMemberSnapshot(10, false, false) } };
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

        var result = await new TrackingPreferenceCommandService(db).SetAsync(1, 10, TrackingChoice.AllOut);

        Assert.False(result.IsError);
        Assert.Empty(await db.VoiceAttendance.ToListAsync());
        Assert.Empty(await db.BackgroundVoicePresences.ToListAsync());
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

        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));
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
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));
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
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));
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
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var roster = new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> { [500] = new[] { new VoiceMemberSnapshot(10, false, false) } };
        var t0 = DateTimeOffset.UtcNow;
        await sessions.ReconcileSessionsAsync(1, roster, t0);
        await sessions.ReconcileSessionsAsync(1, roster, t0.AddMinutes(10));

        Assert.Equal(0, (await db.VoiceAttendance.SingleAsync(a => a.UserId == 10)).TotalMinutes); // alone: paused
    }

    [Fact]
    public async Task TrackingSession_AwardsByVoiceMinutes_OnClose()
    {
        var (db, _) = await SeededAsync();
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));

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
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));
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
