using Muster.Contracts;
using Muster.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Musters;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Services.Tracking;

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

    [Fact]
    public async Task Award_IsIdempotent_OnSameSource()
    {
        var (db, points) = await SeededAsync();
        var awards = new CurrencyService(db, new NullCurrencyEventSink());

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
        var awards = new CurrencyService(db, new NullCurrencyEventSink());
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
        var awards = new CurrencyService(db, new NullCurrencyEventSink());
        var quests = new QuestService(db, awards, new GuildAuthorizationService(db), new RecordingMessageBus());

        var quest = (await quests.PostQuestAsync(new QuestDraft(1, 5, QuestOrigin.Guild, "Recruit", "Bring a friend", points.Id, 100))).Quest!;
        await quests.ClaimAsync(quest.Id, 10);
        await quests.SubmitAsync(quest.Id, 10);
        await quests.ApproveAsync(quest.Id, 10, reviewerId: 5);
        await quests.ApproveAsync(quest.Id, 10, reviewerId: 5); // idempotent

        Assert.Equal(100, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
        Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.SourceType == LedgerSourceType.Quest));
    }

    [Fact]
    public async Task GuildQuest_OwnerCanParticipate_AndOncePerMemberByDefault()
    {
        var (db, points) = await SeededAsync();
        var awards = new CurrencyService(db, new NullCurrencyEventSink());
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
        => await db.LedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == pointsId).SumAsync(e => (long?)e.Amount) ?? 0;

    [Fact]
    public async Task TrackingSession_AwardsByVoiceMinutes_OnClose()
    {
        var (db, _) = await SeededAsync();
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new NullCurrencyEventSink()), new GuildAuthorizationService(db));

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
        var sessions = new TrackingSessionService(db, new CurrencyService(db, new NullCurrencyEventSink()), new GuildAuthorizationService(db));
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        await sessions.ProcessVoiceStateAsync(1, 10, currentChannelId: 500, at: t0);          // join
        await sessions.ProcessVoiceStateAsync(1, 10, currentChannelId: null, at: t0.AddMinutes(15)); // leave

        var attendance = await db.VoiceAttendance.SingleAsync();
        Assert.Null(attendance.OpenSegmentStart);
        Assert.Equal(15, attendance.TotalMinutes);
    }
}
