using Muster.Domain.Enums;

namespace Muster.Domain.Entities;

/// <summary>
/// A board item. Two types share one board: <see cref="MissionType.Quest"/> (claimable task with a
/// submit -> approve flow) and <see cref="MissionType.EventOp"/> (scheduled op with RSVP/attendance).
/// A mission may optionally open a tracking session but never requires one.
/// </summary>
public class Mission
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    public MissionType Type { get; set; }

    /// <summary>Guild quest (minted reward) or player bounty (escrowed reward).</summary>
    public MissionOrigin Origin { get; set; } = MissionOrigin.Guild;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public MissionStatus Status { get; set; } = MissionStatus.Draft;

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

    // Quest-specific
    public DateTimeOffset? Deadline { get; set; }
    public bool IsRepeatable { get; set; }
    public bool RequiresApproval { get; set; } = true;

    // Event-op specific
    public DateTimeOffset? ScheduledStart { get; set; }
    public DateTimeOffset? ScheduledEnd { get; set; }
    public ulong? ChannelId { get; set; }
    public ulong? MessageId { get; set; }
    public Guid? TrackingSessionId { get; set; }

    public List<MissionParticipant> Participants { get; set; } = [];
}

public class MissionParticipant
{
    public Guid Id { get; set; }
    public Guid MissionId { get; set; }
    public ulong UserId { get; set; }

    public MissionParticipantStatus Status { get; set; }
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

    public Mission? Mission { get; set; }
}
