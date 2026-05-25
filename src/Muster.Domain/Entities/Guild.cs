using Muster.Domain.Enums;

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

    /// <summary>When true, a personal quest must be accepted + tiered by a quest manager before it opens for takers.</summary>
    public bool PersonalQuestIntakeApproval { get; set; } = true;

    /// <summary>Whether a personal quest needs a final manager sign-off before payout, and who decides.</summary>
    public FinalApprovalMode FinalApprovalMode { get; set; } = FinalApprovalMode.OwnerChoice;

    // --- Anti-staleness auto-resolve (hours; 0 = disabled) ---

    /// <summary>Hours a personal quest may sit awaiting intake approval before <see cref="IntakeTimeoutAction"/> fires.</summary>
    public int IntakeTimeoutHours { get; set; }
    public StaleIntakeAction IntakeTimeoutAction { get; set; } = StaleIntakeAction.Decline;

    /// <summary>Hours a claimed-but-unsubmitted quest may sit idle before the taker is released back to the board.</summary>
    public int ClaimTimeoutHours { get; set; }

    /// <summary>Hours a submitted quest may wait on its reviewer before <see cref="SubmissionTimeoutAction"/> fires.</summary>
    public int SubmissionTimeoutHours { get; set; }
    public StaleSubmissionAction SubmissionTimeoutAction { get; set; } = StaleSubmissionAction.Approve;

    /// <summary>Hours a personal quest may await final sign-off before <see cref="FinalApprovalTimeoutAction"/> fires.</summary>
    public int FinalApprovalTimeoutHours { get; set; }
    public StaleFinalAction FinalApprovalTimeoutAction { get; set; } = StaleFinalAction.Approve;

    // --- Player guardrails (0 = unlimited) ---

    /// <summary>Cap on a poster's simultaneously non-terminal quests.</summary>
    public int MaxOpenQuestsPerPoster { get; set; }

    /// <summary>Cap on quests a single user may have claimed/submitted/in-revision at once.</summary>
    public int MaxActiveClaimsPerUser { get; set; }

    /// <summary>Cap on revision round-trips before a reviewer must approve or reject (0 = unlimited).</summary>
    public int MaxRevisions { get; set; }

    /// <summary>Bonus POINTS minted to the completer of a guild quest, by difficulty tier (set by managers).</summary>
    public long TierSPoints { get; set; } = 100;
    public long TierAPoints { get; set; } = 75;
    public long TierBPoints { get; set; } = 50;
    public long TierCPoints { get; set; } = 30;
    public long TierDPoints { get; set; } = 15;
    public long TierEPoints { get; set; } = 5;

    /// <summary>Bonus POINTS for a difficulty tier (0 for <see cref="QuestTier.None"/>).</summary>
    public long PointsForTier(QuestTier tier) => tier switch
    {
        QuestTier.S => TierSPoints,
        QuestTier.A => TierAPoints,
        QuestTier.B => TierBPoints,
        QuestTier.C => TierCPoints,
        QuestTier.D => TierDPoints,
        QuestTier.E => TierEPoints,
        _ => 0,
    };
}
