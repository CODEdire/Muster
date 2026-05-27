using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Tracking;

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
