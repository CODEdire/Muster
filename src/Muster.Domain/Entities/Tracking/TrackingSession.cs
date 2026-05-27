using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Tracking;

/// <summary>
/// A bounded window during which channel activity is rewardable. Opened either by binding to a
/// Discord scheduled event or manually by an admin. Rewardable signals are voice presence and an
/// optional reaction check-in; message activity inside the window is recorded stats-only.
/// </summary>
public class TrackingSession
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Human label for the session (manual op title, or the Discord scheduled-event name).</summary>
    public string Name { get; set; } = string.Empty;

    public TrackingSessionSource Source { get; set; }

    /// <summary>Discord scheduled event snowflake when <see cref="Source"/> is DiscordScheduledEvent.</summary>
    public ulong? ScheduledEventId { get; set; }

    /// <summary>Voice channel the session tracks presence in.</summary>
    public ulong VoiceChannelId { get; set; }

    /// <summary>Channel name captured when the session opened (so the web can show a name, not a snowflake).</summary>
    public string VoiceChannelName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public TrackingSessionStatus Status { get; set; } = TrackingSessionStatus.Active;

    public ulong OpenedBy { get; set; }

    /// <summary>Anti-AFK: when true, reward time pauses while a member is self/server muted or deafened.</summary>
    public bool RequireUnmuted { get; set; }

    /// <summary>Anti-AFK: when true, reward time pauses while a member is alone in the channel (&lt;2 humans).</summary>
    public bool RequireNotAlone { get; set; }

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

    /// <summary>Last time the member was seen present in the channel (eligible or not) — distinguishes "still
    /// here but paused" (muted/alone) from "disconnected/left" in the roster view.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    public int TotalMinutes { get; set; }

    /// <summary>Eligible seconds accrued but not yet rolled into a whole minute (sub-minute precision across flushes).</summary>
    public int CarrySeconds { get; set; }

    /// <summary>Start of the currently-open eligible presence segment, or null when the member isn't present
    /// (or isn't currently eligible under the session's anti-AFK guards).</summary>
    public DateTimeOffset? OpenSegmentStart { get; set; }

    public TrackingSession? TrackingSession { get; set; }
}
