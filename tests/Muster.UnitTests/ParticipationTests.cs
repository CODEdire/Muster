using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

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

    [Fact]
    public async Task Award_IsIdempotent_OnSameSource()
    {
        var (db, points) = await SeededAsync();
        var awards = new AwardService(db);

        await awards.AwardAsync(1, 10, points.Id, 50, LedgerSourceType.ManualAward, "src-1", "r");
        await awards.AwardAsync(1, 10, points.Id, 50, LedgerSourceType.ManualAward, "src-1", "r"); // dup

        Assert.Equal(1, await db.LedgerEntries.CountAsync());
        var wallet = await db.Wallets.SingleAsync();
        Assert.Equal(50, wallet.Balance);
    }

    [Fact]
    public async Task Muster_Reaction_RewardsOnce_AndRespectsCapacity()
    {
        var (db, points) = await SeededAsync();
        var awards = new AwardService(db);
        var musters = new MusterService(db, awards, new GuildAuthorizationService(db));

        var muster = await musters.CreateAsync(1, 100, 999, "Roll call", ["✅"], points.Id, 10, capacity: 1, expiresAt: null);

        Assert.Equal(ReactionOutcome.Recorded, await musters.RecordReactionAsync(999, 10, "✅"));
        Assert.Equal(ReactionOutcome.AlreadyParticipated, await musters.RecordReactionAsync(999, 10, "✅"));
        Assert.Equal(ReactionOutcome.Full, await musters.RecordReactionAsync(999, 20, "✅")); // capacity = 1

        Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.SourceType == LedgerSourceType.Muster));
        Assert.Equal(10, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
        _ = muster;
    }

    [Fact]
    public async Task Quest_AwardsOnApproval_AndNotTwice()
    {
        var (db, points) = await SeededAsync();
        var missions = new MissionService(db, new AwardService(db), new GuildAuthorizationService(db));

        var quest = await missions.CreateQuestAsync(1, "Recruit", "Bring a friend", 5, points.Id, 100);
        await missions.ClaimAsync(quest.Id, 10);
        await missions.SubmitAsync(quest.Id, 10);
        await missions.ApproveAsync(quest.Id, 10, reviewerId: 5);
        await missions.ApproveAsync(quest.Id, 10, reviewerId: 5); // idempotent

        Assert.Equal(100, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
        Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.SourceType == LedgerSourceType.Mission));
    }

    [Fact]
    public async Task TrackingSession_AwardsByVoiceMinutes_OnClose()
    {
        var (db, _) = await SeededAsync();
        var sessions = new TrackingSessionService(db, new AwardService(db), new GuildAuthorizationService(db));

        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var joinedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        await sessions.ProcessVoiceStateAsync(1, userId: 10, currentChannelId: 500, at: joinedAt);
        await sessions.CloseAsync(session.Id, at: joinedAt.AddMinutes(30), pointsPerMinute: 2);

        var attendance = await db.VoiceAttendance.SingleAsync();
        Assert.Equal(30, attendance.TotalMinutes);
        Assert.Equal(60, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance); // 30 min * 2
    }

    [Fact]
    public async Task TrackingSession_ClosingSegment_OnChannelLeave()
    {
        var (db, _) = await SeededAsync();
        var sessions = new TrackingSessionService(db, new AwardService(db), new GuildAuthorizationService(db));
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await sessions.ProcessVoiceStateAsync(1, 10, currentChannelId: 500, at: t0);          // join
        await sessions.ProcessVoiceStateAsync(1, 10, currentChannelId: null, at: t0.AddMinutes(15)); // leave

        var attendance = await db.VoiceAttendance.SingleAsync();
        Assert.Null(attendance.OpenSegmentStart);
        Assert.Equal(15, attendance.TotalMinutes);
    }
}
