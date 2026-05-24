using Muster.Domain.Enums;

namespace Muster.Domain.Entities;

/// <summary>
/// A bounded window during which channel activity is rewardable. Opened either by binding to a
/// Discord scheduled event or manually by an admin. Rewardable signals are voice presence and an
/// optional reaction check-in; message activity inside the window is recorded stats-only.
/// </summary>
public class TrackingSession
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    public TrackingSessionSource Source { get; set; }

    /// <summary>Discord scheduled event snowflake when <see cref="Source"/> is DiscordScheduledEvent.</summary>
    public ulong? ScheduledEventId { get; set; }

    /// <summary>Voice channel the session tracks presence in.</summary>
    public ulong VoiceChannelId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public TrackingSessionStatus Status { get; set; } = TrackingSessionStatus.Active;

    public ulong OpenedBy { get; set; }

    public List<VoiceAttendance> Attendance { get; set; } = [];
}

/// <summary>Accumulated voice presence for a member within a tracking session.</summary>
public class VoiceAttendance
{
    public Guid Id { get; set; }
    public Guid TrackingSessionId { get; set; }
    public ulong UserId { get; set; }

    public DateTimeOffset FirstJoinedAt { get; set; }
    public DateTimeOffset? LastLeftAt { get; set; }
    public int TotalMinutes { get; set; }

    public TrackingSession? TrackingSession { get; set; }
}

/// <summary>
/// Raw activity event. Stats-only in v1 (rewards come from voice attendance / musters / missions).
/// </summary>
public class ActivityRecord
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong UserId { get; set; }
    public ActivityType Type { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Discord message snowflake; doubles as the dedupe key for message activity.</summary>
    public ulong? SourceMessageId { get; set; }

    public Guid? TrackingSessionId { get; set; }
}

/// <summary>Daily per-(guild,user,channel) rollup so leaderboards/stats stay cheap.</summary>
public class DailyActivityRollup
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public ulong ChannelId { get; set; }
    public DateOnly Date { get; set; }
    public int MessageCount { get; set; }
    public int VoiceMinutes { get; set; }
}

/// <summary>A reaction "muster" message: react to check in. May carry multiple emoji options.</summary>
public class ReactionMuster
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    /// <summary>Emoji that count toward this muster (each may map to a distinct response).</summary>
    public List<string> Emojis { get; set; } = [];

    /// <summary>Optional cap: only the first N reactors are rewarded.</summary>
    public int? Capacity { get; set; }

    public Guid CurrencyId { get; set; }
    public long RewardAmount { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public List<ReactionParticipant> Participants { get; set; } = [];
}

public class ReactionParticipant
{
    public Guid Id { get; set; }
    public Guid MusterId { get; set; }
    public ulong UserId { get; set; }
    public string Emoji { get; set; } = string.Empty;
    public DateTimeOffset ReactedAt { get; set; }

    public ReactionMuster? Muster { get; set; }
}

/// <summary>A manual or bulk award granted by an admin for off-platform contributions.</summary>
public class ManualAward
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    public Guid CurrencyId { get; set; }
    public long Amount { get; set; }
    public string Reason { get; set; } = string.Empty;

    public ulong AwardedBy { get; set; }
    public DateTimeOffset AwardedAt { get; set; }
}
