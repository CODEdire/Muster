using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Tracking;

/// <summary>
/// Per-channel monitoring rule for the always-on "background" participation plane (distinct from a
/// bounded <see cref="TrackingSession"/>). Admins pick which voice/text channels are watched and how
/// each is rewarded. Background voice accrual is suppressed while a <see cref="TrackingSession"/> is
/// active on the same channel (the session owns that window).
/// </summary>
public class TrackedChannel
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }

    public TrackedChannelKind Kind { get; set; }
    public TrackedChannelMode Mode { get; set; } = TrackedChannelMode.StatsOnly;

    /// <summary>Points awarded per eligible minute of voice presence (Voice + Reward).</summary>
    public int PointsPerMinute { get; set; } = 1;

    /// <summary>Points awarded per message (Text + Reward). Reserved for P2; unused while text is stats-only.</summary>
    public int PointsPerMessage { get; set; }

    /// <summary>Cap on background points a member can earn in this channel per UTC day (0 = uncapped).</summary>
    public int DailyCapPoints { get; set; }

    /// <summary>Anti-AFK: skip members who are self- or server-muted/deafened.</summary>
    public bool RequireUnmuted { get; set; } = true;

    /// <summary>Anti-AFK: skip accrual when fewer than two humans are present in the channel.</summary>
    public bool RequireNotAlone { get; set; } = true;
}
