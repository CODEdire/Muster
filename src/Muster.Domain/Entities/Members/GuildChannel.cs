using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Members;

/// <summary>
/// A guild voice/text channel: both the synced roster entry (so the web can show/pick channels by name
/// without a live Discord call) and its background-tracking config. A row with <see cref="Mode"/> ==
/// <see cref="TrackedChannelMode.Off"/> is just a roster entry; a non-Off row is actively monitored.
///
/// Only standalone voice and text channels are stored — threads, categories, and forums are skipped.
/// When a Discord channel is deleted, an unconfigured row is removed outright, but a configured one is
/// <b>soft-deleted</b> (<see cref="DeletedAt"/> set) so its config + name survive until an admin cleans
/// it up. Channel ids are unique per guild, so <c>(GuildId, ChannelId)</c> is the key everything else
/// (background presence, message-reward state, rollups) references by convention.
/// </summary>
public class GuildChannel
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public string Name { get; set; } = string.Empty;
    public GuildChannelKind Kind { get; set; }

    /// <summary>Discord sort position within the guild (for ordering pickers); null when unknown.</summary>
    public int? Position { get; set; }

    /// <summary>Set when the underlying Discord channel is gone but tracking config is retained; null = live.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    // --- Background-tracking config (Off = roster-only, not monitored) ---

    public TrackedChannelMode Mode { get; set; } = TrackedChannelMode.Off;

    /// <summary>Points awarded per eligible minute of voice presence (Voice + Reward).</summary>
    public int PointsPerMinute { get; set; } = 1;

    /// <summary>Points awarded per reward event (Text + Reward).</summary>
    public int PointsPerMessage { get; set; }

    /// <summary>Messages required per reward event (Text + Reward). 1 = every message; higher rewards sustained chat.</summary>
    public int MessagesPerPoint { get; set; } = 1;

    /// <summary>Minimum seconds between rewarded messages for a member in this channel (anti-burst). 0 = none.</summary>
    public int MessageCooldownSeconds { get; set; }

    /// <summary>Cap on message-reward points a member can earn in this channel per UTC day. 0 = uncapped.</summary>
    public int MessageDailyCapPoints { get; set; }

    /// <summary>Cap on background voice points a member can earn in this channel per UTC day. 0 = uncapped.</summary>
    public int DailyCapPoints { get; set; }

    /// <summary>Anti-AFK guards that pause background voice reward accrual (default: skip deafened + alone, allow muted).</summary>
    public AfkGuards Guards { get; set; } = AfkGuards.Undeafened | AfkGuards.NotAlone;

    /// <summary>Runtime: when this channel last went from empty to occupied (UTC); null when empty. Drives the
    /// channel-wide "minimum time active" multiplier condition — it resets whenever the channel clears out.</summary>
    public DateTimeOffset? OccupiedSince { get; set; }

    /// <summary>True when this channel is actively monitored (config present and not soft-deleted).</summary>
    public bool IsTracked => Mode != TrackedChannelMode.Off && DeletedAt is null;
}
