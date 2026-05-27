namespace Muster.Domain.Entities.Tracking;

/// <summary>
/// Always-on voice accrual state for one member in one monitored channel (the background plane).
/// Unlike <see cref="VoiceAttendance"/> (scoped to a bounded session), this persists across the
/// member's comings and goings and is reconciled against the live voice roster. Eligible presence
/// accrues and is flushed to a points award periodically and on leave.
/// </summary>
public class BackgroundVoicePresence
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public ulong ChannelId { get; set; }

    /// <summary>Start of the current eligible-and-present REWARD window, advanced on each flush; null when the
    /// member isn't present or isn't currently eligible (muted/alone/suppressed).</summary>
    public DateTimeOffset? OpenSegmentStart { get; set; }

    /// <summary>Eligible seconds accrued (reward) but not yet converted into a whole minted minute.</summary>
    public int CarrySeconds { get; set; }

    /// <summary>Start of the current ACTIVE-TIME window (raw presence, unguarded, overlaps everything);
    /// null when the member isn't present in the channel. Stats only — never pays.</summary>
    public DateTimeOffset? ActiveOpenSegmentStart { get; set; }

    /// <summary>Active-time seconds accrued but not yet rolled into a whole stat minute.</summary>
    public int ActiveCarrySeconds { get; set; }

    /// <summary>UTC day the <see cref="AwardedPointsToday"/> counter applies to (rolls over at midnight UTC).</summary>
    public DateOnly AwardedDate { get; set; }

    /// <summary>Background points already minted to this member in this channel on <see cref="AwardedDate"/>.</summary>
    public int AwardedPointsToday { get; set; }
}
