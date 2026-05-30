using Muster.Domain.Enums;
using Muster.Contracts;
using Muster.Infrastructure.Services.Quests;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// Viewer-aware scrub of <see cref="QuestDetailView"/>. The unscrubbed view leaks dispute reasons + per-participant
/// submission/review notes to any guild member — these tests pin the rules a privileged viewer (owner, manager) sees
/// everything, a row's own worker sees their own row, and unrelated viewers see nothing private.
/// </summary>
public class QuestDetailViewScrubTests
{
    private const ulong OwnerId = 100;
    private const ulong WorkerA = 200;
    private const ulong WorkerB = 201;
    private const ulong DisputerId = WorkerA;
    private const ulong RandomMember = 999;
    private const ulong ManagerId = 50;

    private static QuestDetailView SampleDetail() => new(
        Id: Guid.NewGuid(),
        Name: "Operation Test",
        Description: "Find the leak",
        Origin: QuestOrigin.Player,
        Status: QuestStatus.Disputed,
        RewardAmount: 100, RewardCode: "COIN", Tier: QuestTier.None, BonusPoints: 0, Capacity: 2,
        OwnerId: OwnerId, OwnerName: "Owner", OwnerAvatar: null,
        DisputedBy: DisputerId, DisputedByName: "Worker A", DisputeReason: "Owner ghosted me",
        CreatedAt: DateTimeOffset.UtcNow, ScheduledStart: null, Deadline: null,
        Participants: new List<QuestDetailParticipant>
        {
            new(WorkerA, "Worker A", null, QuestParticipantStatus.Submitted, "Did the thing", "Need more detail", ReviewedBy: ManagerId, ReviewedByName: "Mod", SubmittedAt: DateTimeOffset.UtcNow),
            new(WorkerB, "Worker B", null, QuestParticipantStatus.Claimed, "Working on it", null, ReviewedBy: null, ReviewedByName: null, SubmittedAt: null),
        });

    [Fact]
    public void Manager_SeesAllPrivateFields()
    {
        var scrubbed = QuestDetailViewScrub.ForViewer(SampleDetail(), ManagerId, isManager: true);

        Assert.Equal("Owner ghosted me", scrubbed.DisputeReason);
        Assert.Equal(DisputerId, scrubbed.DisputedBy);
        Assert.Equal("Did the thing", scrubbed.Participants.Single(p => p.UserId == WorkerA).Note);
        Assert.Equal("Need more detail", scrubbed.Participants.Single(p => p.UserId == WorkerA).ReviewNote);
        Assert.Equal("Working on it", scrubbed.Participants.Single(p => p.UserId == WorkerB).Note);
    }

    [Fact]
    public void Owner_SeesAllPrivateFields()
    {
        var scrubbed = QuestDetailViewScrub.ForViewer(SampleDetail(), OwnerId, isManager: false);

        Assert.Equal("Owner ghosted me", scrubbed.DisputeReason);
        Assert.Equal("Did the thing", scrubbed.Participants.Single(p => p.UserId == WorkerA).Note);
        Assert.Equal("Need more detail", scrubbed.Participants.Single(p => p.UserId == WorkerA).ReviewNote);
        Assert.Equal("Working on it", scrubbed.Participants.Single(p => p.UserId == WorkerB).Note);
    }

    [Fact]
    public void WorkerA_SeesOwnNoteAndReview_NotWorkerB_NorDispute()
    {
        // WorkerA is also the disputer in this fixture — so they should see the dispute fields too.
        var scrubbed = QuestDetailViewScrub.ForViewer(SampleDetail(), WorkerA, isManager: false);

        // Own row: notes + review note + reviewer visible.
        var rowA = scrubbed.Participants.Single(p => p.UserId == WorkerA);
        Assert.Equal("Did the thing", rowA.Note);
        Assert.Equal("Need more detail", rowA.ReviewNote);
        Assert.Equal(ManagerId, rowA.ReviewedBy);

        // Other worker's row: scrubbed.
        var rowB = scrubbed.Participants.Single(p => p.UserId == WorkerB);
        Assert.Null(rowB.Note);
        Assert.Null(rowB.ReviewNote);

        // Disputer themselves sees the dispute reason (they wrote it).
        Assert.Equal("Owner ghosted me", scrubbed.DisputeReason);
    }

    [Fact]
    public void WorkerB_NonDisputer_DoesNotSeeDisputeReason_OrWorkerANote()
    {
        var scrubbed = QuestDetailViewScrub.ForViewer(SampleDetail(), WorkerB, isManager: false);

        // Dispute fields scrubbed for a non-disputer non-staff viewer.
        Assert.Null(scrubbed.DisputeReason);
        Assert.Null(scrubbed.DisputedBy);
        Assert.Null(scrubbed.DisputedByName);

        // WorkerA's row is scrubbed (B is not staff and not the row's worker).
        var rowA = scrubbed.Participants.Single(p => p.UserId == WorkerA);
        Assert.Null(rowA.Note);
        Assert.Null(rowA.ReviewNote);
        Assert.Null(rowA.ReviewedBy);

        // WorkerB sees their own row.
        var rowB = scrubbed.Participants.Single(p => p.UserId == WorkerB);
        Assert.Equal("Working on it", rowB.Note);
    }

    [Fact]
    public void RandomMember_SeesNothingPrivate()
    {
        var scrubbed = QuestDetailViewScrub.ForViewer(SampleDetail(), RandomMember, isManager: false);

        Assert.Null(scrubbed.DisputeReason);
        Assert.Null(scrubbed.DisputedBy);
        Assert.All(scrubbed.Participants, p =>
        {
            Assert.Null(p.Note);
            Assert.Null(p.ReviewNote);
            Assert.Null(p.ReviewedBy);
            Assert.Null(p.ReviewedByName);
        });

        // Public fields stay (name, status, reward, etc).
        Assert.Equal("Operation Test", scrubbed.Name);
        Assert.Equal(100, scrubbed.RewardAmount);
        Assert.Equal(QuestStatus.Disputed, scrubbed.Status); // dispute existence is public via status
    }

    [Fact]
    public void Scrub_DoesNotMutateOriginal()
    {
        var original = SampleDetail();
        var rowAOriginal = original.Participants.Single(p => p.UserId == WorkerA);

        _ = QuestDetailViewScrub.ForViewer(original, RandomMember, isManager: false);

        // Original instance + its rows are unchanged (record `with` produces copies).
        Assert.Equal("Owner ghosted me", original.DisputeReason);
        Assert.Equal("Did the thing", rowAOriginal.Note);
        Assert.Equal("Need more detail", rowAOriginal.ReviewNote);
    }
}
