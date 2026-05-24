namespace Muster.Domain.Entities;

/// <summary>A Discord server (guild) that the bot is installed in. The tenant boundary.</summary>
public class Guild
{
    /// <summary>Discord guild snowflake.</summary>
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? IconHash { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Discord id of the guild owner — always treated as a bot admin (lockout-proof bypass).</summary>
    public ulong OwnerId { get; set; }

    /// <summary>IANA time zone id used for scheduling and reporting.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    public GuildSettings Settings { get; set; } = new();
}

/// <summary>Per-guild configuration. Owned by <see cref="Guild"/>.</summary>
public class GuildSettings
{
    /// <summary>Discord role ids treated as bot admins, in addition to Manage-Guild holders.</summary>
    public List<ulong> AdminRoleIds { get; set; } = [];

    /// <summary>Discord role ids allowed to approve quests / run ops.</summary>
    public List<ulong> OfficerRoleIds { get; set; } = [];

    /// <summary>Discord role ids allowed to create guild quests and approve/arbitrate player bounties.</summary>
    public List<ulong> QuestManagerRoleIds { get; set; } = [];

    /// <summary>
    /// Discord role ids whose holders may earn rewards / be tracked. **Empty means everyone
    /// participates** (the default); set roles to restrict participation to org members and exclude
    /// guests/newcomers.
    /// </summary>
    public List<ulong> ParticipantRoleIds { get; set; } = [];

    /// <summary>Channels whose activity is recorded (empty = all).</summary>
    public List<ulong> TrackedChannelIds { get; set; } = [];

    /// <summary>When true, quest submissions require officer approval before reward.</summary>
    public bool QuestsRequireApproval { get; set; } = true;

    /// <summary>Points awarded per minute of voice attendance when a tracking session closes.</summary>
    public int PointsPerVoiceMinute { get; set; } = 1;
}
