using Muster.Contracts;
using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Guilds;

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

/// <summary>Per-guild configuration. Owned by <see cref="Guild"/> and stored as one JSON document.</summary>
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

    /// <summary>Points awarded per minute of voice attendance when a tracking session closes.</summary>
    public int PointsPerVoiceMinute { get; set; } = 1;

    /// <summary>
    /// Background-plane consent default for members who haven't set their own preference. <c>false</c> (default)
    /// = opt-out: background tracking is on, members may leave. <c>true</c> = opt-in: members aren't
    /// background-tracked until they explicitly opt in. Does not affect bounded Sessions.
    /// </summary>
    public bool BackgroundTrackingOptIn { get; set; }

    /// <summary>Spendable currency code that a Session mints on close (in addition to POINTS), or null to mint none.</summary>
    public string? SessionCoinCurrencyCode { get; set; }

    /// <summary>Eligible voice minutes per 1 unit of <see cref="SessionCoinCurrencyCode"/> on session close
    /// (floored). 0 (default) disables session coin minting.</summary>
    public int MinutesPerCoin { get; set; }

    /// <summary>When true (default), bounded Sessions honor the same anti-AFK guards as the background plane —
    /// time accrues only while a member is unmuted and not alone in the channel. When false, sessions count
    /// raw presence.</summary>
    public bool ApplyAfkGuardsToSessions { get; set; } = true;

    /// <summary>Auto-close an active session once it has run this many hours (safety net for a session that's
    /// never stopped — a forgotten manual op, a deleted channel, etc.). 0 = never auto-close.</summary>
    public int MaxSessionHours { get; set; } = 24;

    /// <summary>How many days of raw <c>ActivityRecord</c> rows to keep before the prune sweep deletes them
    /// (daily rollups are kept, so stats survive). 0 (default) = keep raw rows forever.</summary>
    public int ActivityRetentionDays { get; set; }

    /// <summary>Minimum seconds a member must accrue in a session to be kept on its attendance roster — drops
    /// drive-by join/leaves so they don't clutter the roster or attendee count. 0 (default) = keep everyone.</summary>
    public int MinTrackedSeconds { get; set; }

    // --- Reward multipliers & presence bonuses (P8) ---

    /// <summary>How overlapping time-window multipliers combine: take the highest active factor (default) or
    /// multiply them together. The role-multiplier factor always multiplies the resulting time factor.</summary>
    public Enums.MultiplierStacking MultiplierStacking { get; set; } = Enums.MultiplierStacking.HighestWins;

    /// <summary>Upper clamp on the final effective multiplier (after stacking + role factor). 0 (default) = no cap.</summary>
    public decimal MultiplierCap { get; set; }

    /// <summary>Flat POINTS bonus awarded on session close to members present at the start (see
    /// <see cref="StartBonusWindowMinutes"/>). 0 (default) = no start bonus.</summary>
    public int SessionStartBonus { get; set; }

    /// <summary>Flat POINTS bonus awarded on session close to members present at the end (see
    /// <see cref="EndBonusWindowMinutes"/>). 0 (default) = no end bonus.</summary>
    public int SessionEndBonus { get; set; }

    /// <summary>A member qualifies for the start bonus if they joined within this many minutes of the session start.</summary>
    public int StartBonusWindowMinutes { get; set; } = 5;

    /// <summary>A member qualifies for the end bonus if they were still present within this many minutes of the session end.</summary>
    public int EndBonusWindowMinutes { get; set; } = 5;

    /// <summary>When true, the active reward multiplier also scales the start/end presence bonuses (start bonus by
    /// the factor at session start, end bonus by the factor at session end). Default false = flat bonuses.</summary>
    public bool MultiplyPresenceBonuses { get; set; }

    /// <summary>How many days of detailed ledger history to keep before the prune sweep folds older rows into a
    /// single carry-forward <c>Checkpoint</c> entry per (user, currency, season). Balances are preserved (the sum is
    /// unchanged); only line-item history is compacted. 0 (default) = never prune (keep full history).</summary>
    public int LedgerRetentionDays { get; set; }

    /// <summary>Quest-board configuration: approval workflow, auto-resolve timeouts, limits, and tier rewards.</summary>
    public QuestSettings Quests { get; set; } = new();
}

/// <summary>The quest-board slice of a guild's configuration.</summary>
public class QuestSettings
{
    /// <summary>Discord channel the bot posts the live quest board to — one auto-updating message per quest,
    /// edited in place as the quest changes state. 0 (default) disables the channel board (it stays pull-only).</summary>
    public ulong QuestChannelId { get; set; }

    /// <summary>Private staff channel for mod-only quest states (pending intake, disputed, awaiting final sign-off) —
    /// the public can't vet/act on those, so their card lives here instead of the public board. The guild restricts
    /// who can see it via Discord channel permissions. 0 (default) = those states post nowhere until a channel is set.</summary>
    public ulong QuestModChannelId { get; set; }

    /// <summary>How long a completed quest's channel card lingers after it goes terminal (Closed/Cancelled/Expired)
    /// before the bot deletes it — enough to show recent activity without cluttering forever. The quest + ledger
    /// stay in the DB (web keeps full history); only the Discord message is removed. 0 = delete as soon as terminal.</summary>
    public int BoardRetentionHours { get; set; } = 48;

    /// <summary>How many hours before a quest's deadline the bot DMs a nudge to its active workers ("closes soon —
    /// submit before then"), and to a player-bounty owner whose quest is about to expire with no taker. Sent once per
    /// quest (tracked by <see cref="Muster.Domain.Entities.Quests.GuildQuest.DeadlineReminderSentAt"/>). 0 disables reminders.</summary>
    public int DeadlineReminderHours { get; set; } = 24;

    /// <summary>When true, quest submissions require officer approval before reward (legacy gate).</summary>
    public bool QuestsRequireApproval { get; set; } = true;

    /// <summary>When true, a personal quest must be accepted + tiered by a quest manager before it opens for takers.</summary>
    public bool PersonalQuestIntakeApproval { get; set; } = true;

    /// <summary>When true, the poster may take and complete their own player quest (escrow round-trips to them).
    /// Off by default — a bounty is normally for others. Handy for solo/small guilds and for testing.</summary>
    public bool AllowSelfParticipation { get; set; }

    /// <summary>Whether a personal quest needs a final manager sign-off before payout, and who decides.</summary>
    public FinalApprovalMode FinalApprovalMode { get; set; } = FinalApprovalMode.OwnerChoice;

    // --- Anti-staleness auto-resolve (hours; 0 = disabled) ---

    /// <summary>Hours a personal quest may sit awaiting intake approval before <see cref="IntakeTimeoutAction"/> fires.</summary>
    public int IntakeTimeoutHours { get; set; }
    public StaleIntakeAction IntakeTimeoutAction { get; set; } = StaleIntakeAction.Decline;

    /// <summary>Hours a claimed-but-unsubmitted quest may sit idle before the taker is released back to the board.</summary>
    public int ClaimTimeoutHours { get; set; }

    /// <summary>Hours a submitted quest may wait on its reviewer before <see cref="SubmissionTimeoutAction"/> fires.
    /// 0 disables auto-resolve: a submitted quest then waits on a human verdict indefinitely and may outlive its
    /// deadline. This is intended — deadline expiry deliberately skips quests with a pending submission (the worker
    /// did their part; the reviewer owes the verdict). Set non-zero to make unreviewed submissions self-resolve.</summary>
    public int SubmissionTimeoutHours { get; set; }
    public StaleSubmissionAction SubmissionTimeoutAction { get; set; } = StaleSubmissionAction.Approve;

    /// <summary>Hours a personal quest may await final sign-off before <see cref="FinalApprovalTimeoutAction"/> fires.</summary>
    public int FinalApprovalTimeoutHours { get; set; }
    public StaleFinalAction FinalApprovalTimeoutAction { get; set; } = StaleFinalAction.Approve;

    /// <summary>Hours a disputed bounty may sit before it auto-resolves. The outcome is fixed and fair —
    /// the timeout favours the party that did NOT raise the dispute (the disputant bears the burden) — so no
    /// action enum is needed. 0 = manual-only (a manager must arbitrate).</summary>
    public int DisputeTimeoutHours { get; set; }

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
