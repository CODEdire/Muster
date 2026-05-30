using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// Pure domain coverage of the quest state machine — no DB. Guards the invariants the services + command
/// handlers rely on: how a draft becomes a quest, the terminal-state lock on <see cref="GuildQuest.TransitionTo"/>,
/// and every <see cref="QuestParticipant"/> verdict transition (valid moves, idempotency, illegal moves throw).
/// </summary>
public class QuestStateMachineTests
{
    private static QuestDraft Draft(
        QuestOrigin origin = QuestOrigin.Guild, long reward = 100, bool requireIntake = false, DateTimeOffset? startsAt = null) =>
        new(GuildId: 1, CreatedBy: 7, origin, "Quest", "desc", RewardCurrencyId: Guid.NewGuid(), reward,
            Deadline: null, StartsAt: startsAt, Tier: QuestTier.None, BonusPoints: 0, Capacity: 1, RequireIntake: requireIntake);

    // ---- GuildQuest.Create -------------------------------------------------

    [Fact]
    public void Create_GuildQuest_OpensImmediately()
    {
        var q = GuildQuest.Create(Draft());
        Assert.Equal(QuestStatus.Open, q.Status);
        Assert.Equal(0, q.EscrowAmount);       // guild quests mint — no escrow held
        Assert.Equal(7u, q.OwnerId);            // owner is the creator
        Assert.Equal(q.CreatedAt, q.StatusChangedAt);
    }

    [Fact]
    public void Create_WithIntakeGate_StartsPendingApproval()
    {
        var q = GuildQuest.Create(Draft(QuestOrigin.Player, requireIntake: true));
        Assert.Equal(QuestStatus.PendingApproval, q.Status);
    }

    [Fact]
    public void Create_WithFutureStart_IsScheduled()
    {
        var q = GuildQuest.Create(Draft(startsAt: DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(QuestStatus.Scheduled, q.Status);
    }

    [Fact]
    public void Create_PlayerBounty_EscrowsTheReward()
    {
        var q = GuildQuest.Create(Draft(QuestOrigin.Player, reward: 50));
        Assert.Equal(50, q.EscrowAmount);
    }

    [Fact]
    public void Create_ClampsCapacityToAtLeastOne()
    {
        var q = GuildQuest.Create(Draft() with { Capacity = 0 });
        Assert.Equal(1, q.Capacity);
    }

    // ---- TransitionTo ------------------------------------------------------

    [Fact]
    public void TransitionTo_SameStatus_IsNoop()
    {
        var q = GuildQuest.Create(Draft());
        var stamp = q.StatusChangedAt = DateTimeOffset.UtcNow.AddHours(-3);
        q.TransitionTo(QuestStatus.Open);
        Assert.Equal(stamp, q.StatusChangedAt); // unchanged — no-op
    }

    [Fact]
    public void TransitionTo_AdvancesStatus_AndStampsTime()
    {
        var q = GuildQuest.Create(Draft());
        q.StatusChangedAt = DateTimeOffset.UtcNow.AddHours(-3);
        q.TransitionTo(QuestStatus.Closed);
        Assert.Equal(QuestStatus.Closed, q.Status);
        Assert.True(q.StatusChangedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Theory]
    [InlineData(QuestStatus.Closed)]
    [InlineData(QuestStatus.Cancelled)]
    [InlineData(QuestStatus.Expired)]
    public void TransitionTo_FromTerminal_Throws(QuestStatus terminal)
    {
        var q = GuildQuest.Create(Draft());
        q.TransitionTo(terminal);
        Assert.Throws<InvalidOperationException>(() => q.TransitionTo(QuestStatus.Open));
    }

    // ---- QuestParticipant verdict transitions ------------------------------

    [Fact]
    public void NewClaim_StartsClaimed()
    {
        var p = QuestParticipant.NewClaim(Guid.NewGuid(), 10);
        Assert.Equal(QuestParticipantStatus.Claimed, p.Status);
        Assert.NotNull(p.ClaimedAt);
    }

    [Fact]
    public void Submit_FromClaimed_RecordsNote()
    {
        var p = QuestParticipant.NewClaim(Guid.NewGuid(), 10);
        p.Submit("done");
        Assert.Equal(QuestParticipantStatus.Submitted, p.Status);
        Assert.Equal("done", p.Note);
        Assert.NotNull(p.SubmittedAt);
    }

    [Fact]
    public void Submit_FromApproved_Throws()
    {
        var p = Submitted();
        p.Approve(1);
        Assert.Throws<InvalidOperationException>(() => p.Submit(null));
    }

    [Fact]
    public void Approve_FromSubmitted_RecordsReviewerAndNote()
    {
        var p = Submitted();
        p.Approve(reviewerId: 1, note: "great work");
        Assert.Equal(QuestParticipantStatus.Approved, p.Status);
        Assert.Equal(1u, p.ReviewedBy);
        Assert.Equal("great work", p.ReviewNote);
    }

    [Fact]
    public void Approve_IsIdempotent()
    {
        var p = Submitted();
        p.Approve(1);
        p.Approve(1); // no throw, stays approved
        Assert.Equal(QuestParticipantStatus.Approved, p.Status);
    }

    [Fact]
    public void Approve_FromClaimed_Throws()
    {
        var p = QuestParticipant.NewClaim(Guid.NewGuid(), 10);
        Assert.Throws<InvalidOperationException>(() => p.Approve(1));
    }

    [Fact]
    public void Reject_FromSubmitted_RecordsReason()
    {
        var p = Submitted();
        p.Reject(reviewerId: 1, note: "incomplete");
        Assert.Equal(QuestParticipantStatus.Rejected, p.Status);
        Assert.Equal("incomplete", p.ReviewNote);
    }

    [Fact]
    public void Reject_FromApproved_Throws()
    {
        var p = Submitted();
        p.Approve(1);
        Assert.Throws<InvalidOperationException>(() => p.Reject(1));
    }

    [Fact]
    public void RequestRevision_FromSubmitted_IncrementsCount()
    {
        var p = Submitted();
        p.RequestRevision(reviewerId: 1, note: "fix it");
        Assert.Equal(QuestParticipantStatus.RevisionRequested, p.Status);
        Assert.Equal(1, p.RevisionCount);
        Assert.Equal("fix it", p.ReviewNote);

        p.Submit("fixed");                 // resubmit is allowed from RevisionRequested
        Assert.Equal(QuestParticipantStatus.Submitted, p.Status);
    }

    [Fact]
    public void RequestRevision_FromClaimed_Throws()
    {
        var p = QuestParticipant.NewClaim(Guid.NewGuid(), 10);
        Assert.Throws<InvalidOperationException>(() => p.RequestRevision(1, null));
    }

    [Theory]
    [InlineData(QuestParticipantStatus.Claimed)]
    [InlineData(QuestParticipantStatus.Submitted)]
    [InlineData(QuestParticipantStatus.RevisionRequested)]
    public void Release_FromActiveStates_MarksReleased(QuestParticipantStatus from)
    {
        var p = QuestParticipant.NewClaim(Guid.NewGuid(), 10);
        if (from is QuestParticipantStatus.Submitted or QuestParticipantStatus.RevisionRequested) { p.Submit(null); }
        if (from == QuestParticipantStatus.RevisionRequested) { p.RequestRevision(1, null); }

        p.Release("timed out");
        Assert.Equal(QuestParticipantStatus.Released, p.Status);
    }

    [Fact]
    public void Release_FromApproved_IsNoop()
    {
        var p = Submitted();
        p.Approve(1);
        p.Release();
        Assert.Equal(QuestParticipantStatus.Approved, p.Status); // a verdict is never silently released
    }

    [Fact]
    public void Reopen_FromRejected_RestoresSubmitted_AndClearsNote()
    {
        var p = Submitted();
        p.Reject(1, "wrong");
        p.Reopen();
        Assert.Equal(QuestParticipantStatus.Submitted, p.Status);
        Assert.Null(p.ReviewNote);
    }

    [Fact]
    public void Reopen_FromSubmitted_Throws()
    {
        var p = Submitted();
        Assert.Throws<InvalidOperationException>(() => p.Reopen());
    }

    private static QuestParticipant Submitted()
    {
        var p = QuestParticipant.NewClaim(Guid.NewGuid(), 10);
        p.Submit(null);
        return p;
    }
}
