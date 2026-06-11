using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>
/// The unified quest board: guild quests (minted) and personal bounties (escrowed) over one set of
/// <see cref="GuildQuest"/>/<see cref="QuestParticipant"/> tables. Callers depend on this interface, not the
/// concrete service. State transitions return a <see cref="QuestResult"/>; reads return the entities directly.
/// </summary>
public interface IQuestService
{
    /// <summary>Post a quest of either funding model (escrows for a personal quest; mints for a guild quest).</summary>
    Task<(QuestResult Result, GuildQuest? Quest)> PostQuestAsync(QuestDraft draft, CancellationToken ct = default);

    /// <summary>Open or scheduled guild quests.</summary>
    Task<IReadOnlyList<GuildQuest>> ListOpenQuestsAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>Live player bounties.</summary>
    Task<IReadOnlyList<GuildQuest>> ListOpenAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>Flip due scheduled quests to Open in one guild.</summary>
    Task<int> ActivateScheduledAsync(ulong guildId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Flip due scheduled quests to Open across all guilds (scheduled sweep).</summary>
    Task<int> ActivateScheduledAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Expire past-deadline guild quests (no refund — minted on approval).</summary>
    Task<int> ExpireDueQuestsAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Refund + expire past-deadline bounties in one guild.</summary>
    Task<int> ExpireDueAsync(ulong guildId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Refund + expire past-deadline bounties across all guilds (scheduled sweep).</summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Cancel an uncompleted guild quest (no escrow to refund).</summary>
    Task<QuestResult> CancelQuestAsync(Guid questId, CancellationToken ct = default);

    /// <summary>Claim/take a quest (guild or personal); honours once-per-member and capacity.</summary>
    Task<QuestResult> ClaimAsync(Guid questId, ulong userId, CancellationToken ct = default);

    /// <summary>Submit a claimed quest for review.</summary>
    Task<QuestResult> SubmitAsync(Guid questId, ulong userId, string? note = null, CancellationToken ct = default);

    /// <summary>Send a submitted quest back to revise (guild: by member + reviewer; personal: by owner).</summary>
    Task<QuestResult> RequestRevisionAsync(Guid questId, ulong actorId, ulong? memberId, string? note, CancellationToken ct = default);

    /// <summary>Auto-release idle claimers past the cutoff (no submission); returns the released members. Quest stays Open.</summary>
    Task<IReadOnlyList<ulong>> ReleaseStaleClaimsAsync(Guid questId, DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>Release one member's active claim/submission, freeing the slot (status → Released, re-claimable). The
    /// quest stays Open. <paramref name="actorId"/> distinguishes a self-abandon from a manager force-release (for the
    /// notice + audit note); authorization is enforced by the caller's handler.</summary>
    Task<QuestResult> ReleaseClaimAsync(Guid questId, ulong targetUserId, ulong actorId, CancellationToken ct = default);

    /// <summary>Patch a quest's editable fields before anyone works on it (reward/tier/capacity are guild-only).</summary>
    Task<QuestResult> EditAsync(Guid questId, string? name, string? description, long? reward, DateTimeOffset? deadline, QuestTier? tier, int? capacity, Guid? questTypeId = null, CancellationToken ct = default);

    /// <summary>Approve a guild-quest submission and mint the reward (optional feedback note).</summary>
    Task<QuestResult> ApproveAsync(Guid questId, ulong userId, ulong reviewerId, string? note = null, CancellationToken ct = default);

    /// <summary>Reject a guild-quest submission (no reward). Final per member; reverse with <see cref="ReopenRejectionAsync"/>.</summary>
    Task<QuestResult> RejectAsync(Guid questId, ulong userId, ulong reviewerId, string? note = null, CancellationToken ct = default);

    /// <summary>Reverse an accidental guild-quest reject: restore the member's Rejected submission to Submitted.</summary>
    Task<QuestResult> ReopenRejectionAsync(Guid questId, ulong userId, ulong reviewerId, CancellationToken ct = default);

    /// <summary>Owner confirms a personal quest → escrow pays the completer (or routes to final sign-off).</summary>
    Task<QuestResult> ConfirmAsync(Guid questId, ulong ownerId, CancellationToken ct = default);

    /// <summary>Owner cancels an unsubmitted personal quest → refund.</summary>
    Task<QuestResult> CancelAsync(Guid questId, ulong ownerId, CancellationToken ct = default);

    /// <summary>Owner/taker disputes a submitted personal quest → manager arbitration.</summary>
    Task<QuestResult> DisputeAsync(Guid questId, ulong userId, string? reason = null, CancellationToken ct = default);

    /// <summary>Manager arbitrates a dispute: pay the completer or refund the owner. <paramref name="reviewerId"/>
    /// is the arbiter (recusal applies; pass 0 for the system arbiter / dispute-timeout sweep).</summary>
    Task<QuestResult> ArbitrateAsync(Guid questId, ulong reviewerId, bool payCompleter, CancellationToken ct = default);

    /// <summary>Manager accepts a pending personal quest at intake (assigns tier/bonus, opens it).</summary>
    Task<QuestResult> AcceptAsync(Guid questId, ulong reviewerId, QuestTier tier, long bonusPoints, bool? requireFinalApproval, CancellationToken ct = default);

    /// <summary>Manager rejects a pending personal quest at intake → refund the owner.</summary>
    Task<QuestResult> RejectIntakeAsync(Guid questId, ulong reviewerId, CancellationToken ct = default);

    /// <summary>Manager finalizes a personal quest awaiting sign-off: pay the completer or refund the owner.</summary>
    Task<QuestResult> FinalizeAsync(Guid questId, ulong reviewerId, bool pay, CancellationToken ct = default);
}
