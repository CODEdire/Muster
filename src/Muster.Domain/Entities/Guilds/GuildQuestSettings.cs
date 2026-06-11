using Muster.Contracts;
using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Guilds;

/// <summary>
/// Per-guild quest-board configuration — its own table (table-per-feature, like <see cref="GuildShopSettings"/> and
/// <see cref="GuildMusterSettings"/>), not the <c>GuildSettings</c> JSON blob. Flat columns for the scalars. Also
/// bound from configuration via <c>IOptions</c> to provide the platform default values used to seed a guild's row the
/// first time it's written. Mirrors the legacy owned <see cref="QuestSettings"/> field-for-field, plus a new
/// <see cref="QuestsEnabled"/> master gate.
/// </summary>
public class GuildQuestSettings
{
    /// <summary>Owning guild — primary key + FK (1:1 with <c>Guild</c>). 0 on the IOptions defaults template.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Master per-guild switch for the quest feature (the per-guild layer of the feature gate). New field —
    /// not present on the legacy <see cref="QuestSettings"/>; defaults on so migrated guilds keep quests enabled.</summary>
    public bool QuestsEnabled { get; set; } = true;

    // --- Channels ---

    /// <summary>Discord channel the bot posts the live quest board to (0 = pull-only).</summary>
    public ulong QuestChannelId { get; set; }

    /// <summary>Private staff channel for mod-only quest states (pending intake, disputed, awaiting final sign-off).
    /// 0 = those states post nowhere until a channel is set.</summary>
    public ulong QuestModChannelId { get; set; }

    /// <summary>How long a completed quest's channel card lingers after it goes terminal before deletion. 0 = delete
    /// as soon as terminal. The quest + ledger stay in the DB regardless (web keeps full history).</summary>
    public int BoardRetentionHours { get; set; } = 48;

    /// <summary>How many hours before a quest's deadline the bot DMs a nudge to active workers / a stalled owner.
    /// 0 disables reminders.</summary>
    public int DeadlineReminderHours { get; set; } = 24;

    // --- Approval workflow ---

    /// <summary>When true, quest submissions require officer approval before reward (legacy gate).</summary>
    public bool QuestsRequireApproval { get; set; } = true;

    /// <summary>When true, a personal quest must be accepted + tiered by a quest manager before it opens for takers.</summary>
    public bool PersonalQuestIntakeApproval { get; set; } = true;

    /// <summary>When true, the poster may take and complete their own player quest.</summary>
    public bool AllowSelfParticipation { get; set; }

    /// <summary>Whether a personal quest needs a final manager sign-off before payout, and who decides.</summary>
    public FinalApprovalMode FinalApprovalMode { get; set; } = FinalApprovalMode.OwnerChoice;

    // --- Anti-staleness auto-resolve (hours; 0 = disabled) ---

    /// <summary>Hours a personal quest may sit awaiting intake approval before <see cref="IntakeTimeoutAction"/> fires.</summary>
    public int IntakeTimeoutHours { get; set; }
    public StaleIntakeAction IntakeTimeoutAction { get; set; } = StaleIntakeAction.Decline;

    /// <summary>Hours a claimed-but-unsubmitted quest may sit idle before the taker is released back to the board.</summary>
    public int ClaimTimeoutHours { get; set; }

    /// <summary>Hours a submitted quest may wait on its reviewer before <see cref="SubmissionTimeoutAction"/> fires
    /// (0 = wait on a human verdict indefinitely).</summary>
    public int SubmissionTimeoutHours { get; set; }
    public StaleSubmissionAction SubmissionTimeoutAction { get; set; } = StaleSubmissionAction.Approve;

    /// <summary>Hours a personal quest may await final sign-off before <see cref="FinalApprovalTimeoutAction"/> fires.</summary>
    public int FinalApprovalTimeoutHours { get; set; }
    public StaleFinalAction FinalApprovalTimeoutAction { get; set; } = StaleFinalAction.Approve;

    /// <summary>Hours a disputed bounty may sit before it auto-resolves (favouring the non-disputant). 0 = manual-only.</summary>
    public int DisputeTimeoutHours { get; set; }

    // --- Player guardrails (0 = unlimited) ---

    /// <summary>Cap on a poster's simultaneously non-terminal quests.</summary>
    public int MaxOpenQuestsPerPoster { get; set; }

    /// <summary>Cap on quests a single user may have claimed/submitted/in-revision at once.</summary>
    public int MaxActiveClaimsPerUser { get; set; }

    /// <summary>Cap on revision round-trips before a reviewer must approve or reject (0 = unlimited).</summary>
    public int MaxRevisions { get; set; }

    // --- Tier reward points ---

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

    /// <summary>Build a new table row from the legacy owned <see cref="QuestSettings"/> (the forward-migration mapper).
    /// Copies every field; <see cref="QuestsEnabled"/> defaults on (it has no legacy equivalent).</summary>
    public static GuildQuestSettings FromLegacy(ulong guildId, QuestSettings src) => new()
    {
        GuildId = guildId,
        QuestChannelId = src.QuestChannelId,
        QuestModChannelId = src.QuestModChannelId,
        BoardRetentionHours = src.BoardRetentionHours,
        DeadlineReminderHours = src.DeadlineReminderHours,
        QuestsRequireApproval = src.QuestsRequireApproval,
        PersonalQuestIntakeApproval = src.PersonalQuestIntakeApproval,
        AllowSelfParticipation = src.AllowSelfParticipation,
        FinalApprovalMode = src.FinalApprovalMode,
        IntakeTimeoutHours = src.IntakeTimeoutHours,
        IntakeTimeoutAction = src.IntakeTimeoutAction,
        ClaimTimeoutHours = src.ClaimTimeoutHours,
        SubmissionTimeoutHours = src.SubmissionTimeoutHours,
        SubmissionTimeoutAction = src.SubmissionTimeoutAction,
        FinalApprovalTimeoutHours = src.FinalApprovalTimeoutHours,
        FinalApprovalTimeoutAction = src.FinalApprovalTimeoutAction,
        DisputeTimeoutHours = src.DisputeTimeoutHours,
        MaxOpenQuestsPerPoster = src.MaxOpenQuestsPerPoster,
        MaxActiveClaimsPerUser = src.MaxActiveClaimsPerUser,
        MaxRevisions = src.MaxRevisions,
        TierSPoints = src.TierSPoints,
        TierAPoints = src.TierAPoints,
        TierBPoints = src.TierBPoints,
        TierCPoints = src.TierCPoints,
        TierDPoints = src.TierDPoints,
        TierEPoints = src.TierEPoints,
    };
}
