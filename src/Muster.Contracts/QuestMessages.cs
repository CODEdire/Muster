namespace Muster.Contracts;

// Quest-board message contracts: the lifecycle notification + the CQRS command set invoked via IMessageBus by
// every surface (bot/web/api). The handler is the single funnel where authorization lives. (See GuildQuest.md §0.)

/// <summary>
/// Emitted by the quest service whenever a quest transitions. Consumers (a Discord notifier, an external
/// connector, …) subscribe and fan out per <see cref="Moment"/>. <paramref name="TargetUserId"/> is who
/// should hear about it (null = quest managers / board).
/// </summary>
public record QuestLifecycleNotified(
    ulong GuildId,
    Guid QuestId,
    string QuestName,
    QuestLifecycleMoment Moment,
    ulong? TargetUserId,
    string Detail);

/// <summary>Approve a member's guild-quest submission and mint their reward (GuildMaster only). Optional note = praise/feedback.</summary>
public record ApproveQuestSubmission(ulong GuildId, Guid QuestId, ulong MemberId, ulong ReviewerId, string? Note = null) : IGuildCommand
{
    public ulong ActorId => ReviewerId;
}

/// <summary>Reject a member's guild-quest submission — no reward (GuildMaster only). Optional note = reason.</summary>
public record RejectQuestSubmission(ulong GuildId, Guid QuestId, ulong MemberId, ulong ReviewerId, string? Note = null) : IGuildCommand
{
    public ulong ActorId => ReviewerId;
}

/// <summary>Reverse an accidental guild-quest rejection — restore the submission for review (GuildMaster only).</summary>
public record ReopenQuestRejection(ulong GuildId, Guid QuestId, ulong MemberId, ulong ReviewerId) : IGuildCommand
{
    public ulong ActorId => ReviewerId;
}

/// <summary>Claim a quest to work on it (guild or personal — the handler routes by origin).</summary>
public record ClaimQuest(ulong GuildId, Guid QuestId, ulong UserId) : IGuildCommand
{
    public ulong ActorId => UserId;
}

/// <summary>Submit a claimed quest for review.</summary>
public record SubmitQuest(ulong GuildId, Guid QuestId, ulong UserId, string? Note) : IGuildCommand
{
    public ulong ActorId => UserId;
}

/// <summary>Post a quest. A guild quest mints its reward on approval (GuildMaster only); a player bounty
/// escrows the poster's own balance. Dates are UTC (adapters parse the user's local time). The handler
/// validates input, resolves the currency, applies guild guardrails, and routes by <see cref="QuestOrigin"/>.</summary>
public record PostQuest(
    ulong GuildId,
    ulong ActorId,
    QuestOrigin Origin,
    string Name,
    string CurrencyCode,
    long Reward,
    string Description,
    DateTimeOffset? StartsAt,
    DateTimeOffset? Deadline,
    QuestTier Tier,
    bool RequestFinalApproval,
    int Capacity,
    Guid? QuestTypeId = null) : IGuildCommand;

/// <summary>Cancel a quest (guild → manager; player bounty → owner, with escrow refund). Routes by origin.</summary>
public record CancelQuest(ulong GuildId, Guid QuestId, ulong ActorId) : IGuildCommand;

/// <summary>Owner confirms a personal quest is complete → pay the completer (or route to final sign-off).</summary>
public record ConfirmQuest(ulong GuildId, Guid QuestId, ulong ActorId) : IGuildCommand;

/// <summary>Owner or taker disputes a submitted personal quest → manager arbitration.</summary>
public record DisputeQuest(ulong GuildId, Guid QuestId, ulong ActorId, string? Reason = null) : IGuildCommand;

/// <summary>Arbitrate a disputed personal quest: pay the completer or refund the owner (GuildMaster; recusal applies).</summary>
public record ArbitrateQuest(ulong GuildId, Guid QuestId, ulong ActorId, bool Pay) : IGuildCommand;

/// <summary>Send a submitted quest back to revise (guild → manager picks the member; player → owner). Routes by origin.</summary>
public record RequestQuestRevision(ulong GuildId, Guid QuestId, ulong ActorId, ulong? MemberId, string? Note) : IGuildCommand;

/// <summary>Accept a pending personal quest at intake, assigning its difficulty tier (GuildMaster only).</summary>
public record AcceptQuestIntake(ulong GuildId, Guid QuestId, ulong ActorId, QuestTier Tier, bool RequireFinalApproval) : IGuildCommand;

/// <summary>Reject a pending personal quest at intake → refund the owner (GuildMaster only).</summary>
public record RejectQuestIntake(ulong GuildId, Guid QuestId, ulong ActorId) : IGuildCommand;

/// <summary>Final sign-off on a personal quest awaiting it: pay the completer or refund the owner (GuildMaster only).</summary>
public record FinalizeQuest(ulong GuildId, Guid QuestId, ulong ActorId, bool Pay) : IGuildCommand;

/// <summary>Patch a quest before anyone works on it (guild → manager; player → owner). Blank/null fields are
/// left unchanged; reward/tier/capacity apply to guild quests only.</summary>
public record EditQuest(
    ulong GuildId, Guid QuestId, ulong ActorId,
    string? Name, string? Description, long? Reward, DateTimeOffset? Deadline, QuestTier? Tier, int? Capacity,
    Guid? QuestTypeId = null) : IGuildCommand;

/// <summary>Release an active claim/submission, freeing the slot (the participation goes to <c>Released</c>, so the
/// member may re-claim). Self-abandon when <see cref="TargetUserId"/> == <see cref="ActorId"/> (any taker); otherwise
/// a manager force-release of someone else's claim. The quest stays Open.</summary>
public record ReleaseQuestClaim(ulong GuildId, Guid QuestId, ulong ActorId, ulong TargetUserId) : IGuildCommand;
