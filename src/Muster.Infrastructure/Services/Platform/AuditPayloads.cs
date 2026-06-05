using Muster.Contracts;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Platform;

/// <summary>
/// Strongly-typed payload records for audit entries. Each record serializes to a small JSON blob stored on
/// <see cref="Muster.Domain.Entities.Operations.AuditLog.Details"/>. Formatters in the web layer deserialize
/// to render rich tokens.
/// </summary>
/// <remarks>
/// Naming convention: <c>{Action}Payload</c>. Fields are positional records, immutable, and small — only the
/// data needed to reconstruct what happened. Resolution of names/avatars happens at render time via the audit
/// lookup batcher, not here.
/// </remarks>
public static class AuditPayloads { }

// ---- Configuration ---------------------------------------------------------

/// <summary>A role-mapping toggle. <see cref="Added"/> = the role gained the kind; false = it was removed.</summary>
public record RoleTogglePayload(string Kind, ulong RoleId, string RoleName, bool Added);

/// <summary>A ledger retention change. 0 = inherit the platform default.</summary>
public record LedgerRetentionPayload(int OldDays, int NewDays);

/// <summary>A multiplier (recurring/role/one-off) was added or removed.</summary>
public record MultiplierPayload(Guid MultiplierId, string Name, decimal Factor, MultiplierScope Scope);

// ---- Currency --------------------------------------------------------------

/// <summary>One bulk currency adjustment queued — fan-out happens asynchronously via the worker.</summary>
public record BulkQueuePayload(string CurrencyCode, long Delta, int TargetCount, string Reason, Guid? BatchId);

/// <summary>Staff balance correction (mint when <see cref="Delta"/> &gt; 0, deduction when &lt; 0).</summary>
public record CurrencyAdjustPayload(string CurrencyCode, ulong UserId, long Delta, string Reason, string? ExternalId);

/// <summary>Member-to-member transfer of a spendable currency.</summary>
public record CurrencyTransferPayload(string CurrencyCode, ulong FromUserId, ulong ToUserId, long Amount, string Reason);

/// <summary>Connector-driven inbound mint to a member's wallet.</summary>
public record CurrencyMintPayload(string CurrencyCode, ulong UserId, long Amount, string Reason, string? ExternalId);

/// <summary>Connector-driven spend (debit) of a member's wallet.</summary>
public record CurrencySpendPayload(string CurrencyCode, ulong UserId, long Amount, string Reason, string? ExternalId);

/// <summary>Background balance sync triggered for one External/Hybrid currency.</summary>
public record CurrencySyncAllPayload(string CurrencyCode);

/// <summary>An outbound connector call mirrored a movement to the external economy. <see cref="Kind"/> is
/// <c>add</c> (credit) or <c>remove</c> (debit); <see cref="Status"/> is the HTTP status the target returned.
/// Pairs with the ledger entry for the same movement (ledger = balance, this = the API call).</summary>
public record CurrencyConnectorPushPayload(string CurrencyCode, ulong UserId, long Amount, string Kind, int Status);

/// <summary>One periodic reconcile sweep over an External/Hybrid currency: how many wallets were due, reconciled,
/// and failed. Emitted once per currency per sweep run (not per member) to keep the trail readable.</summary>
public record CurrencySyncSweepPayload(string CurrencyCode, int Candidates, int Synced, int Failed);

/// <summary>A reconcile applied an unusually large correction (|delta| ≥ threshold) — a signal of external
/// tampering or an integration bug, surfaced for an auditor even though the per-member reconcile is otherwise
/// ledger-only.</summary>
public record CurrencyDriftAnomalyPayload(string CurrencyCode, ulong UserId, long Delta, long ExternalBalance);

/// <summary>The connector setup for a currency (URL + auth shape) was saved.</summary>
public record CurrencyConnectorPayload(string CurrencyCode, CurrencyMode Mode);

/// <summary>Wallet cache rebuild from the ledger — how many balances diverged + were corrected.</summary>
public record CurrencyRebuildPayload(int Corrected);

/// <summary>Webhook delete carries the same shape as the other webhook payloads but with no enabled flag.</summary>
public record WebhookDeletePayload(Guid WebhookId, string Url);

// ---- Tracking --------------------------------------------------------------

/// <summary>Settings save on the Tracking page — captures every adjustable knob in one payload so the formatter can
/// diff against the previous row.</summary>
public record TrackingSettingsPayload(
    bool BackgroundOptIn,
    AfkGuards DefaultBackgroundGuards, AfkGuards DefaultSessionGuards, AfkGuards DefaultEventGuards,
    string? CoinCurrency, int MinutesPerCoin, int? MaxSessionHours,
    int? ActivityRetentionDays, int? MinTrackedSeconds,
    MultiplierStacking Stacking, decimal? Cap,
    decimal StartBonus, decimal EndBonus,
    int StartWindowMinutes, int EndWindowMinutes, bool MultiplyBonuses);

/// <summary>A tracked channel was removed.</summary>
public record TrackedChannelRemovePayload(ulong ChannelId, string? ChannelName);

// ---- Multipliers -----------------------------------------------------------

/// <summary>A multiplier was enabled or disabled.</summary>
public record MultiplierTogglePayload(Guid MultiplierId, bool Enabled);

// ---- Quests ----------------------------------------------------------------

/// <summary>A quest was posted. Origin distinguishes player-bounty vs guild quests.</summary>
public record QuestPostPayload(
    Guid QuestId, QuestOrigin Origin, string Name, string CurrencyCode, long Reward,
    QuestTier Tier, int Capacity, DateTimeOffset? StartsAt, DateTimeOffset? Deadline);

/// <summary>A quest was edited before anyone worked on it. Null fields = unchanged.</summary>
public record QuestEditPayload(
    Guid QuestId, string? Name, string? Description, long? Reward,
    DateTimeOffset? Deadline, QuestTier? Tier, int? Capacity);

/// <summary>A quest was cancelled by the owner/manager (escrow refunded for player bounties).</summary>
public record QuestCancelPayload(Guid QuestId);

/// <summary>A member claimed a quest.</summary>
public record QuestClaimPayload(Guid QuestId, ulong UserId);

/// <summary>A member submitted a claimed quest for review (with an optional note).</summary>
public record QuestSubmitPayload(Guid QuestId, ulong UserId, string? Note);

/// <summary>Review verdict on a submission — covers Approve / Reject / ReopenRejection.</summary>
public record QuestReviewPayload(Guid QuestId, ulong MemberId, string? Note);

/// <summary>Owner confirmed a personal quest as complete.</summary>
public record QuestConfirmPayload(Guid QuestId);

/// <summary>Owner or taker disputed a submitted personal quest.</summary>
public record QuestDisputePayload(Guid QuestId, string? Reason);

/// <summary>Arbitration on a disputed quest — pay the completer or refund the owner.</summary>
public record QuestArbitratePayload(Guid QuestId, bool Pay);

/// <summary>A reviewer sent a submission back for revisions, optionally targeting a specific member.</summary>
public record QuestRevisionPayload(Guid QuestId, ulong? MemberId, string? Note);

/// <summary>Intake decision — accept (with assigned tier + final-approval requirement).</summary>
public record QuestAcceptIntakePayload(Guid QuestId, QuestTier Tier, bool RequireFinalApproval);

/// <summary>Intake decision — reject (escrow refunded).</summary>
public record QuestRejectIntakePayload(Guid QuestId);

/// <summary>Final sign-off on a personal quest awaiting it — pay the completer or refund.</summary>
public record QuestFinalizePayload(Guid QuestId, bool Pay);

/// <summary>A claim was released (self-abandon when target == actor; otherwise manager force-release).</summary>
public record QuestReleaseClaimPayload(Guid QuestId, ulong TargetUserId);

// ---- Configuration (page-form payloads) ------------------------------------

/// <summary>Quest board channel config saved — board/mod channels + how long the board persists posts.</summary>
public record QuestChannelPayload(ulong? BoardChannelId, ulong? ModChannelId, int RetentionHours);

/// <summary>Quest automation timeouts + their stale-resolution actions, plus the limits saved in one row.</summary>
public record QuestAutomationPayload(
    int IntakeHours, StaleIntakeAction IntakeAction,
    int ClaimHours,
    int SubmissionHours, StaleSubmissionAction SubmissionAction,
    int FinalHours, StaleFinalAction FinalAction,
    int MaxOpenPerPoster, int MaxActiveClaims, int MaxRevisions, int DeadlineReminderHours);

/// <summary>Approval-flow knobs: whether intake needs a manager, the final sign-off mode, and self-review rules.</summary>
public record QuestApprovalPayload(bool RequireIntakeApproval, FinalApprovalMode FinalMode, bool AllowSelfReview);

/// <summary>Tier point totals (S–E).</summary>
public record QuestTierPointsPayload(long S, long A, long B, long C, long D, long E);

/// <summary>A currency was created or updated. <see cref="OldName"/> is null on Create.</summary>
public record CurrencyDefinitionPayload(string Code, string Name, string? OldName, bool IsSeasonal, bool IsSpendable, CurrencyMode Mode);

/// <summary>A currency webhook was created/toggled/deleted.</summary>
public record WebhookPayload(Guid WebhookId, string Url, bool? Enabled);

// ---- Seasons ---------------------------------------------------------------

/// <summary>A season was started.</summary>
public record SeasonStartPayload(Guid SeasonId, string Name);

/// <summary>A season was ended.</summary>
public record SeasonEndPayload(Guid SeasonId, string Name, DateTimeOffset EndedAt);

// ---- Tracking --------------------------------------------------------------

/// <summary>A tracking session was started or stopped.</summary>
public record SessionPayload(Guid SessionId, string Name, ulong VoiceChannelId, string? VoiceChannelName);

/// <summary>A channel was added or removed from the tracked set.</summary>
public record TrackedChannelPayload(ulong ChannelId, string? ChannelName, GuildChannelKind Kind, TrackedChannelMode Mode);

// ---- API clients -----------------------------------------------------------

/// <summary>An API client was created or revoked. <see cref="Scopes"/> empty for revoke.</summary>
public record ApiClientPayload(Guid ClientId, string Name, IReadOnlyList<string> Scopes, ulong? ActsAsUserId);

// ---- Membership ------------------------------------------------------------

/// <summary>A member sync was requested from Discord (no targets — sync is guild-wide).</summary>
public record MemberSyncPayload(string Reason);
