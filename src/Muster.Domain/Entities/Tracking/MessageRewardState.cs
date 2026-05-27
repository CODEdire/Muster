namespace Muster.Domain.Entities.Tracking;

/// <summary>
/// Per-(guild, user, channel) anti-spam state for message rewards: drives the messages-per-point counter,
/// the cooldown between rewarded messages, and the per-day cap. Separate from <see cref="DailyActivityRollup"/>
/// (which is stats); this is purely the reward gate.
/// </summary>
public class MessageRewardState
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public ulong ChannelId { get; set; }

    /// <summary>Messages counted since the last reward event (compared against <c>MessagesPerPoint</c>).</summary>
    public int MessagesSinceAward { get; set; }

    /// <summary>When the member last earned a message reward here (for the cooldown gate).</summary>
    public DateTimeOffset? LastRewardAt { get; set; }

    /// <summary>UTC day the <see cref="AwardedPointsToday"/> counter applies to.</summary>
    public DateOnly AwardedDate { get; set; }

    /// <summary>Message-reward points already earned in this channel on <see cref="AwardedDate"/>.</summary>
    public int AwardedPointsToday { get; set; }
}
