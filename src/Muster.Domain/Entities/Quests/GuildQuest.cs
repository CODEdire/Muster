using Muster.Contracts;
using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Quests;

/// <summary>
/// The inputs to create a quest, for either funding model. Guild quests set tier/bonus/capacity/repeatable;
/// personal quests (escrowed bounties) set the intake/final-approval flags and are single-taker. The shared
/// fields (name, currency, amount, schedule) carry for both.
/// </summary>
public record QuestDraft(
    ulong GuildId,
    ulong CreatedBy,
    QuestOrigin Origin,
    string Name,
    string Description,
    Guid RewardCurrencyId,
    long RewardAmount,
    DateTimeOffset? Deadline = null,
    DateTimeOffset? StartsAt = null,
    QuestTier Tier = QuestTier.None,
    long BonusPoints = 0,
    int Capacity = 1,
    bool RequireIntake = false,
    bool RequireFinalApproval = false);

/// <summary>
/// A board quest with a claim → submit → approve flow. Two funding models share this aggregate, split by
/// <see cref="Origin"/>: a guild quest mints its reward on approval; a player bounty escrows the poster's
/// reward at post time and pays it out on settlement. (Scheduled events are a separate aggregate, <see cref="GuildEvent"/>.)
/// </summary>
public class GuildQuest
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Guild quest (minted reward) or player bounty (escrowed reward).</summary>
    public QuestOrigin Origin { get; set; } = QuestOrigin.Guild;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public QuestStatus Status { get; set; } = QuestStatus.Draft;

    /// <summary>When the quest last changed status — drives anti-staleness auto-resolve timeouts.</summary>
    public DateTimeOffset StatusChangedAt { get; set; }

    /// <summary>Optimistic-concurrency token so two actors can't both settle the same quest.</summary>
    public byte[]? RowVersion { get; set; }

    public ulong CreatedBy { get; set; }

    /// <summary>Owner: creator for guild quests; the bounty poster for player quests (escrow source/refundee).</summary>
    public ulong OwnerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid RewardCurrencyId { get; set; }
    public long RewardAmount { get; set; }

    /// <summary>Difficulty tier for a guild quest (drives <see cref="BonusPoints"/> via guild config).</summary>
    public QuestTier Tier { get; set; } = QuestTier.None;

    /// <summary>Bonus POINTS minted to the completer on settlement, resolved from the guild's tier config.</summary>
    public long BonusPoints { get; set; }

    /// <summary>When true, a personal quest needs a quest manager's final sign-off before the completer is paid.</summary>
    public bool RequiresFinalApproval { get; set; }

    /// <summary>How many completers may be rewarded (guild quests). Personal quests are single-taker (1).</summary>
    public int Capacity { get; set; } = 1;

    /// <summary>Currency held in escrow for a player bounty (0 when none / refunded / paid out).</summary>
    public long EscrowAmount { get; set; }

    public DateTimeOffset? Deadline { get; set; }

    /// <summary>When the pre-deadline "closes soon" reminder was last sent for this quest (null = not yet). The
    /// reminder fires once; editing the <see cref="Deadline"/> clears this so a fresh deadline nudges again.</summary>
    public DateTimeOffset? DeadlineReminderSentAt { get; set; }

    /// <summary>When set in the future, the quest is created Scheduled and activated at this time.</summary>
    public DateTimeOffset? ScheduledStart { get; set; }

    /// <summary>Who raised the active dispute (owner or taker). Drives the fair dispute-timeout default:
    /// the timeout favours the non-disputing party, so whoever invoked the dispute loses if it lapses.</summary>
    public ulong? DisputedBy { get; set; }

    /// <summary>The disputant's stated reason, surfaced to the arbiter (optional).</summary>
    public string? DisputeReason { get; set; }

    public List<QuestParticipant> Participants { get; set; } = [];

    /// <summary>
    /// Build a new quest from a <see cref="QuestDraft"/>, deriving the starting status (intake gate →
    /// PendingApproval, future start → Scheduled, else Open) and escrow (the reward for player bounties, none
    /// for minted guild quests). Owner is the creator — for a guild quest that's the manager who posted it.
    /// </summary>
    public static GuildQuest Create(QuestDraft draft)
    {
        var scheduled = draft.StartsAt is { } s && s > DateTimeOffset.UtcNow;
        var status = draft.RequireIntake ? QuestStatus.PendingApproval
            : scheduled ? QuestStatus.Scheduled : QuestStatus.Open;
        var now = DateTimeOffset.UtcNow;

        return new GuildQuest
        {
            Id = Guid.NewGuid(),
            GuildId = draft.GuildId,
            Origin = draft.Origin,
            Name = draft.Name.Trim(),
            Description = (draft.Description ?? string.Empty).Trim(),
            Status = status,
            StatusChangedAt = now,
            CreatedBy = draft.CreatedBy,
            OwnerId = draft.CreatedBy,
            CreatedAt = now,
            RewardCurrencyId = draft.RewardCurrencyId,
            RewardAmount = draft.RewardAmount,
            Tier = draft.Tier,
            BonusPoints = draft.BonusPoints,
            Capacity = Math.Max(1, draft.Capacity),
            EscrowAmount = draft.Origin == QuestOrigin.Player ? draft.RewardAmount : 0,
            RequiresFinalApproval = draft.RequireFinalApproval,
            Deadline = draft.Deadline,
            ScheduledStart = draft.StartsAt,
        };
    }

    /// <summary>
    /// Move to a new status, keeping <see cref="StatusChangedAt"/> in lock-step. Idempotent: moving to the
    /// current status is a no-op. Throws if the quest is already terminal — a finished quest can't move.
    /// Callers enforce the business rules for <i>which</i> transition is allowed; this guards the invariant.
    /// </summary>
    public void TransitionTo(QuestStatus next)
    {
        if (Status == next)
        {
            return;
        }

        if (Status is QuestStatus.Closed or QuestStatus.Cancelled or QuestStatus.Expired)
        {
            throw new InvalidOperationException($"Quest {Id} is in terminal state {Status}; cannot transition to {next}.");
        }

        Status = next;
        StatusChangedAt = DateTimeOffset.UtcNow;
    }
}

public class QuestParticipant
{
    public Guid Id { get; set; }
    public Guid QuestId { get; set; }
    public ulong UserId { get; set; }

    public QuestParticipantStatus Status { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public ulong? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>The worker's submission note.</summary>
    public string? Note { get; set; }

    /// <summary>The reviewer's reason when sending back for revision or rejecting.</summary>
    public string? ReviewNote { get; set; }

    /// <summary>How many times this submission has been sent back for revision.</summary>
    public int RevisionCount { get; set; }

    public GuildQuest? Quest { get; set; }

    /// <summary>A fresh claim on a quest (guild claim / bounty take).</summary>
    public static QuestParticipant NewClaim(Guid questId, ulong userId) => new()
    {
        Id = Guid.NewGuid(),
        QuestId = questId,
        UserId = userId,
        Status = QuestParticipantStatus.Claimed,
        ClaimedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Worker submits their work for review. Valid from Claimed or RevisionRequested.</summary>
    public void Submit(string? note)
    {
        if (Status is not (QuestParticipantStatus.Claimed or QuestParticipantStatus.RevisionRequested))
        {
            throw new InvalidOperationException($"Participant {UserId} cannot submit from {Status}.");
        }

        Status = QuestParticipantStatus.Submitted;
        SubmittedAt = DateTimeOffset.UtcNow;
        Note = note;

        // A fresh submission supersedes the prior reviewer feedback, so it doesn't shadow the new note on the card.
        ReviewNote = null;
        ReviewedBy = null;
        ReviewedAt = null;
    }

    /// <summary>Approve the submission (records the reviewer + optional feedback note). Idempotent; valid from Submitted.</summary>
    public void Approve(ulong reviewerId, string? note = null)
    {
        if (Status == QuestParticipantStatus.Approved)
        {
            return;
        }

        if (Status != QuestParticipantStatus.Submitted)
        {
            throw new InvalidOperationException($"Participant {UserId} cannot be approved from {Status}.");
        }

        Status = QuestParticipantStatus.Approved;
        ReviewedBy = reviewerId;
        ReviewedAt = DateTimeOffset.UtcNow;
        if (note is not null)
        {
            ReviewNote = note;
        }
    }

    /// <summary>Reject the submission (records the reviewer). Not valid once approved.</summary>
    public void Reject(ulong reviewerId, string? note = null)
    {
        if (Status is QuestParticipantStatus.Approved)
        {
            throw new InvalidOperationException($"Participant {UserId} cannot be rejected from {Status}.");
        }

        Status = QuestParticipantStatus.Rejected;
        ReviewedBy = reviewerId;
        ReviewedAt = DateTimeOffset.UtcNow;
        ReviewNote = note;
    }

    /// <summary>Send the submission back to revise and resubmit (records the reviewer). Valid from Submitted.</summary>
    public void RequestRevision(ulong reviewerId, string? note)
    {
        if (Status != QuestParticipantStatus.Submitted)
        {
            throw new InvalidOperationException($"Participant {UserId} cannot be sent for revision from {Status}.");
        }

        Status = QuestParticipantStatus.RevisionRequested;
        ReviewedBy = reviewerId;
        ReviewedAt = DateTimeOffset.UtcNow;
        ReviewNote = note;
        RevisionCount++;
    }

    /// <summary>
    /// Give up an active slot (Claimed/Submitted/RevisionRequested) without a verdict — idle-claim timeout or
    /// quest cancel/expire. A no-op for any other status, so it's safe to call across a whole participant list.
    /// Marks <see cref="QuestParticipantStatus.Released"/> (not Rejected), so the member may re-claim later.
    /// </summary>
    public void Release(string? note = null)
    {
        if (Status is not (QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted or QuestParticipantStatus.RevisionRequested))
        {
            return;
        }

        Status = QuestParticipantStatus.Released;
        if (note is not null)
        {
            ReviewNote = note;
        }
    }

    /// <summary>Reverse an accidental/wrong reject: restore the rejected submission to <see cref="QuestParticipantStatus.Submitted"/>
    /// so the reviewer can approve or request-revision correctly. Valid only from Rejected.</summary>
    public void Reopen()
    {
        if (Status != QuestParticipantStatus.Rejected)
        {
            throw new InvalidOperationException($"Participant {UserId} cannot be reopened from {Status}.");
        }

        Status = QuestParticipantStatus.Submitted;
        ReviewNote = null;
    }
}
