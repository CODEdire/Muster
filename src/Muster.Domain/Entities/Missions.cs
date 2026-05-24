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
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public MissionStatus Status { get; set; } = MissionStatus.Draft;

    public ulong CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Guid RewardCurrencyId { get; set; }
    public long RewardAmount { get; set; }

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
    public DateTimeOffset? SubmittedAt { get; set; }
    public ulong? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    public Mission? Mission { get; set; }
}
