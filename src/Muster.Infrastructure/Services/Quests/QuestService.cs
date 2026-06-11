using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Wolverine;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>Outcome of a quest state transition. Ok plus the reasons a transition can be refused.</summary>
public enum QuestResult
{
    Ok,
    NotFound,
    NotEligible,
    NotOpen,
    NotStarted,
    Full,
    InsufficientFunds,
    NotSpendable,
    Forbidden,
    InvalidState,
    /// <summary>The member has already been reviewed on this (non-repeatable) quest — approved or rejected.</summary>
    AlreadyParticipated,
    /// <summary>The member is already working on the guild's per-user maximum number of quests.</summary>
    AtClaimLimit,
}

/// <summary>
/// The unified quest board. One service drives both quest funding models over the same
/// <see cref="GuildQuest"/>/<see cref="QuestParticipant"/> tables, split by <see cref="QuestOrigin"/>:
///
/// <list type="bullet">
/// <item><b>Guild quests</b> (<see cref="QuestOrigin.Guild"/>): the reward is <i>minted</i> on approval.
/// Flow: claim → submit → manager approve (mints; closes at capacity unless repeatable).</item>
/// <item><b>Personal quests</b> (<see cref="QuestOrigin.Player"/>): the poster's coins are <i>escrowed</i>
/// at post time. Flow: post (hold) → take → submit → owner confirm / finalize / dispute → arbitrate
/// (escrow payout or refund). Intake and final-approval workflows live here too.</item>
/// </list>
///
/// Every money-moving transition commits the ledger legs together with the status change in one
/// transaction, so state and ledger can never diverge. Awards are idempotent per (quest, user), so
/// approving/redelivering never double-rewards.
/// </summary>
// Public (not internal) so Wolverine's generated handler code can inline-construct it instead of falling
// back to service location (forbidden by default in Wolverine 6). Adapters still depend on IQuestService.
public sealed class QuestService(
    MusterDbContext db,
    ICurrencyService currency,
    GuildAuthorizationService auth,
    IMessageBus bus) : IQuestService
{
    private static string Key(Guid questId) => $"bounty:{questId}";

    // ---------------------------------------------------------------------------------------------
    // Creation (both origins)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Post a quest of either funding model. Guild quests mint on approval and persist immediately; personal
    /// quests escrow the poster's reward — the poster must be an eligible participant, and the bounty is only
    /// persisted if the hold succeeds (atomic). Returns the created quest, or a failure result.
    /// </summary>
    public async Task<(QuestResult Result, GuildQuest? Quest)> PostQuestAsync(QuestDraft draft, CancellationToken ct = default)
    {
        if (draft.RewardAmount <= 0 || string.IsNullOrWhiteSpace(draft.Name))
        {
            return (QuestResult.InvalidState, null);
        }

        // A personal quest is funded from the poster's own balance, so they must be eligible to participate.
        if (draft.Origin == QuestOrigin.Player && !await auth.IsParticipantAsync(draft.GuildId, draft.CreatedBy, ct))
        {
            return (QuestResult.NotEligible, null);
        }

        var quest = GuildQuest.Create(draft);

        if (draft.Origin == QuestOrigin.Player)
        {
            // Reserve the reward into escrow; only persist the bounty if funding succeeds.
            var hold = await currency.HoldAsync(draft.GuildId, draft.CreatedBy, quest.RewardCurrencyId, quest.EscrowAmount, Key(quest.Id), ct);
            if (hold != EscrowStatus.Ok)
            {
                return (hold.ToQuestResult(), null);
            }
        }

        db.Quests.Add(quest);
        await db.SaveChangesAsync(ct);

        await bus.NotifyCreatedAsync(quest);
        return (QuestResult.Ok, quest);
    }

    /// <summary>Open or scheduled guild quests (player-funded bounties are listed separately).</summary>
    public async Task<IReadOnlyList<GuildQuest>> ListOpenQuestsAsync(ulong guildId, CancellationToken ct = default)
        => await db.ListOpenGuildQuestsAsync(guildId, ct);

    /// <summary>Flip scheduled quests (both origins) in one guild whose start time has arrived to Open.</summary>
    public Task<int> ActivateScheduledAsync(ulong guildId, DateTimeOffset now, CancellationToken ct = default)
        => ActivateAsync(guildId, now, ct);

    /// <summary>Flip scheduled quests across all guilds whose start time has arrived to Open. For the scheduled sweep.</summary>
    public Task<int> ActivateScheduledAsync(DateTimeOffset now, CancellationToken ct = default)
        => ActivateAsync(null, now, ct);

    private async Task<int> ActivateAsync(ulong? guildId, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.ListScheduledDueAsync(now, guildId, ct);
        foreach (var quest in due)
        {
            quest.TransitionTo(QuestStatus.Open);
            // Announce the open so the channel board posts the card now (scheduled quests aren't posted until they open).
            await bus.NotifyAsync(quest, QuestLifecycleMoment.Created, null, "Quest opened.");
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    /// <summary>
    /// Expire past-deadline guild quests across all guilds. Guild quests mint their reward on approval,
    /// so there's nothing to refund — expiring just closes the unfinished quest. Quests with a submission
    /// awaiting approval are left alone. Player-funded quests are expired (and refunded) by <see cref="ExpireDueAsync(DateTimeOffset, CancellationToken)"/>.
    /// </summary>
    public async Task<int> ExpireDueQuestsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var due = await db.ListExpiredGuildQuestsAsync(now, ct);

        var expired = due.Where(m => m.Participants.All(p => p.Status != QuestParticipantStatus.Submitted)).ToList();
        foreach (var quest in expired)
        {
            quest.TransitionTo(QuestStatus.Expired);
        }

        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    /// <summary>Cancel a guild quest that hasn't been completed (no escrow to refund — the reward is minted on approval).
    /// Authorization (manager) is enforced by the caller / command handler.</summary>
    public async Task<QuestResult> CancelQuestAsync(Guid questId, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Status is not (QuestStatus.Open or QuestStatus.Scheduled))
        {
            return QuestResult.InvalidState;
        }

        quest.TransitionTo(QuestStatus.Cancelled);
        await db.SaveChangesAsync(ct);
        return QuestResult.Ok;
    }

    public async Task<QuestResult> ClaimAsync(Guid questId, ulong userId, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Status == QuestStatus.Scheduled)
        {
            return QuestResult.NotStarted;
        }

        if (quest.Status != QuestStatus.Open)
        {
            return QuestResult.NotOpen;
        }

        if (!await auth.IsParticipantAsync(quest.GuildId, userId, ct))
        {
            return QuestResult.NotEligible;
        }

        // A personal quest is the poster's own bounty — they can't take it (unless the guild opts in via
        // AllowSelfParticipation). A guild quest is owned by the guild, so its creator may participate freely.
        if (quest.Origin == QuestOrigin.Player && userId == quest.OwnerId)
        {
            var settings = await db.GetQuestSettingsAsync(quest.GuildId, ct);
            if (!settings.AllowSelfParticipation)
            {
                return QuestResult.Forbidden;
            }
        }

        var mine = quest.Participants.Where(p => p.UserId == userId).ToList();
        if (mine.Any(p => p.Status is QuestParticipantStatus.Claimed
            or QuestParticipantStatus.Submitted or QuestParticipantStatus.RevisionRequested))
        {
            return QuestResult.Ok; // already actively working on it
        }

        // One completion per member: an Approved or (manager-)Rejected outcome is final and bars a re-claim.
        // A Released slot (idle timeout / cancel cleanup) is NOT a verdict, so it doesn't bar them.
        if (mine.Any(p => p.Status is QuestParticipantStatus.Approved or QuestParticipantStatus.Rejected))
        {
            return QuestResult.AlreadyParticipated;
        }

        // Capacity caps concurrent active workers; total completions are bounded by the close-on-approval rule.
        var active = quest.Participants.Count(p => p.Status is QuestParticipantStatus.Claimed
            or QuestParticipantStatus.Submitted or QuestParticipantStatus.RevisionRequested);
        if (active >= Math.Max(1, quest.Capacity))
        {
            return QuestResult.Full;
        }

        db.QuestParticipants.Add(QuestParticipant.NewClaim(questId, userId));
        await db.SaveChangesAsync(ct);
        // Target the claimer so they get the auto-DM with their Submit action.
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Claimed, userId, $"You claimed **{quest.Name}** — submit when you're done.");
        return QuestResult.Ok;
    }

    /// <summary>Approve a guild quest submission and award the reward. Idempotent per (quest, user).</summary>
    public async Task<QuestResult> ApproveAsync(Guid questId, ulong userId, ulong reviewerId, string? note = null, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Origin != QuestOrigin.Guild)
        {
            return QuestResult.InvalidState; // personal bounties settle via confirm/finalize — never mint
        }

        var participant = quest.Participants.FirstOrDefault(p => p.UserId == userId && p.Status == QuestParticipantStatus.Submitted);
        if (participant is null)
        {
            return QuestResult.NotFound;
        }

        participant.Approve(reviewerId, note);

        // A guild quest closes once Capacity distinct completions are reached.
        var approvedCount = quest.Participants.Count(p => p.Status == QuestParticipantStatus.Approved);
        if (approvedCount >= Math.Max(1, quest.Capacity))
        {
            quest.TransitionTo(QuestStatus.Closed);
        }

        await db.SaveChangesAsync(ct);

        // Awards are keyed by the participation, so a member's repeat completions each pay out exactly once.
        await currency.AwardAsync(
            quest.GuildId, userId, quest.RewardCurrencyId, quest.RewardAmount,
            CurrencyLedgerSource.Quest, $"quest:{questId}:participant:{participant.Id}",
            $"Quest approved: {quest.Name}", ct);

        if (quest.BonusPoints > 0)
        {
            await currency.AwardPointsAsync(
                quest.GuildId, userId, quest.BonusPoints,
                CurrencyLedgerSource.Quest, $"quest:{questId}:participant:{participant.Id}:bonus",
                $"Quest bonus: {quest.Name}", ct);
        }

        await bus.NotifyAsync(quest, QuestLifecycleMoment.Settled, userId, $"Approved — reward granted to <@{userId}>.");
        return QuestResult.Ok;
    }

    /// <summary>Final reject of a guild-quest submission (no reward). The quest stays open for another taker.</summary>
    public async Task<QuestResult> RejectAsync(Guid questId, ulong userId, ulong reviewerId, string? note = null, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Origin != QuestOrigin.Guild)
        {
            return QuestResult.InvalidState; // personal bounties are sent back / disputed, not manager-rejected
        }

        var participant = quest.Participants.FirstOrDefault(p => p.UserId == userId && p.Status == QuestParticipantStatus.Submitted);
        if (participant is null)
        {
            return QuestResult.NotFound;
        }

        participant.Reject(reviewerId, note);
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Rejected, userId, "Submission rejected.");
        return QuestResult.Ok;
    }

    /// <summary>
    /// Reverse an accidental / wrong-member reject on a guild quest: the member's Rejected submission is
    /// restored to Submitted for re-review. Allowed only while the quest is Open with a free slot (so a
    /// closed/full quest can't be reopened into). The reviewer then approves or requests revision.
    /// </summary>
    public async Task<QuestResult> ReopenRejectionAsync(Guid questId, ulong userId, ulong reviewerId, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Origin != QuestOrigin.Guild || quest.Status != QuestStatus.Open)
        {
            return QuestResult.InvalidState;
        }

        var participant = quest.Participants.FirstOrDefault(p => p.UserId == userId && p.Status == QuestParticipantStatus.Rejected);
        if (participant is null)
        {
            return QuestResult.NotFound;
        }

        var active = quest.Participants.Count(p => p.Status is QuestParticipantStatus.Claimed
            or QuestParticipantStatus.Submitted or QuestParticipantStatus.RevisionRequested);
        if (active >= Math.Max(1, quest.Capacity))
        {
            return QuestResult.Full; // no slot to reopen into
        }

        participant.Reopen();
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Reopened, userId, "Your submission was reopened for review.");
        return QuestResult.Ok;
    }

    /// <summary>
    /// Patch a quest's editable fields (blank/null keeps the current value). Allowed only before anyone is
    /// actively working on it (no Claimed/Submitted/RevisionRequested/Approved participant) and while it's
    /// still Open/Scheduled/PendingApproval. Reward/tier/capacity are guild-quest-only (a personal reward is
    /// escrowed — cancel and repost to change it). Authorization is enforced by the caller.
    /// </summary>
    public async Task<QuestResult> EditAsync(
        Guid questId, string? name, string? description, long? reward,
        DateTimeOffset? deadline, QuestTier? tier, int? capacity, Guid? questTypeId = null, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Status is not (QuestStatus.Open or QuestStatus.Scheduled or QuestStatus.PendingApproval))
        {
            return QuestResult.InvalidState;
        }

        if (quest.Participants.Any(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted
            or QuestParticipantStatus.RevisionRequested or QuestParticipantStatus.Approved))
        {
            return QuestResult.InvalidState; // edits lock after the first claim
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            quest.Name = name.Trim();
        }

        if (description is not null)
        {
            quest.Description = description.Trim();
        }

        if (deadline.HasValue)
        {
            quest.Deadline = deadline;
            quest.DeadlineReminderSentAt = null; // new deadline → let the "closes soon" nudge fire again
        }

        // Quest type applies to either origin. null = leave unchanged; Guid.Empty = clear; else set (if it's a
        // real type in this guild — an unknown id is ignored rather than clearing the existing one).
        if (questTypeId is { } qt)
        {
            quest.QuestTypeId = qt == Guid.Empty ? null
                : await db.QuestTypes.AnyAsync(t => t.GuildId == quest.GuildId && t.Id == qt, ct) ? qt : quest.QuestTypeId;
        }

        // Reward / tier / capacity are guild-quest-only.
        if (quest.Origin == QuestOrigin.Guild)
        {
            if (reward is { } r)
            {
                if (r <= 0)
                {
                    return QuestResult.InvalidState;
                }

                quest.RewardAmount = r;
            }

            if (tier is { } t)
            {
                quest.Tier = t;
                quest.BonusPoints = (await db.GetQuestSettingsAsync(quest.GuildId, ct)).PointsForTier(t);
            }

            if (capacity is { } cap)
            {
                quest.Capacity = Math.Max(1, cap);
            }
        }

        await db.SaveChangesAsync(ct);
        // Refresh the channel board with the edited details.
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Created, null, "Quest updated.");
        return QuestResult.Ok;
    }

    // ---------------------------------------------------------------------------------------------
    // Personal quests (escrowed bounties)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Owner confirms completion → escrow pays the completer (atomic with status change).</summary>
    public async Task<QuestResult> ConfirmAsync(Guid questId, ulong ownerId, CancellationToken ct = default)
    {
        var quest = await LoadPersonalAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.OwnerId != ownerId)
        {
            return QuestResult.Forbidden;
        }

        if (quest.Status != QuestStatus.Open)
        {
            return QuestResult.InvalidState;
        }

        var taker = quest.Participants.FirstOrDefault(p => p.Status == QuestParticipantStatus.Submitted);
        if (taker is null)
        {
            return QuestResult.InvalidState; // nothing submitted to confirm
        }

        if (quest.RequiresFinalApproval)
        {
            // Owner accepts, but a quest manager must sign off before any money or points move.
            quest.TransitionTo(QuestStatus.PendingFinal);
            await db.SaveChangesAsync(ct);
            await bus.NotifyAsync(quest, QuestLifecycleMoment.AwaitingFinalApproval, null, "Owner accepted — awaiting a manager's final sign-off.");
            return QuestResult.Ok;
        }

        return await PaySettleAsync(quest, taker, ownerId, ct);
    }

    /// <summary>Owner cancels an open bounty that hasn't been submitted → refund (atomic).</summary>
    public async Task<QuestResult> CancelAsync(Guid questId, ulong ownerId, CancellationToken ct = default)
    {
        var quest = await LoadPersonalAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.OwnerId != ownerId)
        {
            return QuestResult.Forbidden;
        }

        if (quest.Status is not (QuestStatus.Open or QuestStatus.Scheduled or QuestStatus.PendingApproval)
            || quest.Participants.Any(p => p.Status == QuestParticipantStatus.Submitted))
        {
            return QuestResult.InvalidState; // after submission, confirm or dispute instead
        }

        await RefundAndReleaseAsync(quest, QuestStatus.Cancelled, ct);
        await db.SaveChangesAsync(ct);
        return QuestResult.Ok;
    }

    /// <summary>Owner or taker raises a dispute on a submitted bounty → Quest Manager arbitration. No money moves.</summary>
    public async Task<QuestResult> DisputeAsync(Guid questId, ulong userId, string? reason = null, CancellationToken ct = default)
    {
        var quest = await LoadPersonalAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        var taker = quest.Participants.FirstOrDefault(p => p.Status == QuestParticipantStatus.Submitted);
        if (quest.Status != QuestStatus.Open || taker is null)
        {
            return QuestResult.InvalidState;
        }

        if (userId != quest.OwnerId && userId != taker.UserId)
        {
            return QuestResult.Forbidden;
        }

        quest.DisputedBy = userId; // drives the fair timeout default (burden on the disputant)
        quest.DisputeReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        quest.TransitionTo(QuestStatus.Disputed);
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Disputed, null, "Dispute raised — a quest manager will arbitrate.");
        return QuestResult.Ok;
    }

    /// <summary>
    /// Resolve a dispute: pay the completer or refund the owner (atomic). <paramref name="reviewerId"/> is the
    /// arbiter; recusal applies — a party to the dispute (owner or taker) can't rule on their own case. Pass
    /// <c>0</c> for the system arbiter (the dispute-timeout sweep), which is never a party.
    /// </summary>
    public async Task<QuestResult> ArbitrateAsync(Guid questId, ulong reviewerId, bool payCompleter, CancellationToken ct = default)
    {
        var quest = await LoadPersonalAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Status != QuestStatus.Disputed)
        {
            return QuestResult.InvalidState;
        }

        var taker = quest.Participants.FirstOrDefault(p => p.Status == QuestParticipantStatus.Submitted);

        // Recusal: no one judges their own case. The system arbiter (0) is never a party.
        if (reviewerId != 0 && (reviewerId == quest.OwnerId || reviewerId == taker?.UserId))
        {
            return QuestResult.Forbidden;
        }

        if (payCompleter && taker is not null)
        {
            return await PaySettleAsync(quest, taker, reviewerId, ct);
        }

        await RefundAndReleaseAsync(quest, QuestStatus.Cancelled, ct);
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Refunded, quest.OwnerId, "Dispute resolved — owner refunded.");
        return QuestResult.Ok;
    }

    /// <summary>
    /// Quest manager accepts a pending personal quest at intake: assigns the difficulty tier (and its bonus
    /// POINTS) and opens it for takers. Optionally sets the final-approval requirement when the guild lets the
    /// approver decide. No money moves (escrow was already reserved at post). Authorization is enforced by the caller.
    /// </summary>
    public async Task<QuestResult> AcceptAsync(
        Guid questId, ulong reviewerId, QuestTier tier, long bonusPoints, bool? requireFinalApproval, CancellationToken ct = default)
    {
        var quest = await LoadPersonalAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Status != QuestStatus.PendingApproval)
        {
            return QuestResult.InvalidState;
        }

        quest.Tier = tier;
        quest.BonusPoints = bonusPoints;
        if (requireFinalApproval is { } rf)
        {
            quest.RequiresFinalApproval = rf;
        }

        var scheduled = quest.ScheduledStart is { } s && s > DateTimeOffset.UtcNow;
        quest.TransitionTo(scheduled ? QuestStatus.Scheduled : QuestStatus.Open);
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Accepted, quest.OwnerId, "Accepted and opened for takers.");
        return QuestResult.Ok;
    }

    /// <summary>Quest manager rejects a personal quest at intake → refund the owner's escrow and cancel.</summary>
    public async Task<QuestResult> RejectIntakeAsync(Guid questId, ulong reviewerId, CancellationToken ct = default)
    {
        var quest = await LoadPersonalAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Status != QuestStatus.PendingApproval)
        {
            return QuestResult.InvalidState;
        }

        await RefundAndReleaseAsync(quest, QuestStatus.Cancelled, ct);
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.RejectedAtIntake, quest.OwnerId, "Rejected at intake — escrow refunded.");
        return QuestResult.Ok;
    }

    /// <summary>Quest manager finalizes a personal quest awaiting sign-off: pay the completer (+ bonus) or refund the owner.</summary>
    public async Task<QuestResult> FinalizeAsync(Guid questId, ulong reviewerId, bool pay, CancellationToken ct = default)
    {
        var quest = await LoadPersonalAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        if (quest.Status != QuestStatus.PendingFinal)
        {
            return QuestResult.InvalidState;
        }

        var taker = quest.Participants.FirstOrDefault(p => p.Status == QuestParticipantStatus.Submitted);
        if (pay && taker is not null)
        {
            return await PaySettleAsync(quest, taker, reviewerId, ct);
        }

        await RefundAndReleaseAsync(quest, QuestStatus.Cancelled, ct);
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Refunded, quest.OwnerId, "Final sign-off declined — owner refunded.");
        return QuestResult.Ok;
    }

    public async Task<IReadOnlyList<GuildQuest>> ListOpenAsync(ulong guildId, CancellationToken ct = default)
        => await db.ListOpenPersonalQuestsAsync(guildId, ct);

    /// <summary>Refund and expire open, past-deadline bounties that haven't been submitted, in one guild.</summary>
    public Task<int> ExpireDueAsync(ulong guildId, DateTimeOffset now, CancellationToken ct = default)
        => ExpireAsync(guildId, now, ct);

    /// <summary>Refund and expire past-deadline bounties across all guilds. For the scheduled sweep.</summary>
    public Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default)
        => ExpireAsync(null, now, ct);

    private async Task<int> ExpireAsync(ulong? guildId, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.ListExpiredPersonalQuestsAsync(now, guildId, ct);

        var expired = 0;
        foreach (var quest in due.Where(m => m.Participants.All(p => p.Status != QuestParticipantStatus.Submitted)))
        {
            await RefundAndReleaseAsync(quest, QuestStatus.Expired, ct);
            expired++;
        }

        await db.SaveChangesAsync(ct);
        return expired;
    }

    // ---------------------------------------------------------------------------------------------
    // Shared lifecycle (both origins)
    // ---------------------------------------------------------------------------------------------

    public async Task<QuestResult> SubmitAsync(Guid questId, ulong userId, string? note = null, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        var participant = quest.Participants.FirstOrDefault(p => p.UserId == userId
            && p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.RevisionRequested);
        if (participant is null)
        {
            return QuestResult.InvalidState;
        }

        participant.Submit(note);
        await db.SaveChangesAsync(ct);

        var toOwner = quest.Origin == QuestOrigin.Player;
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Submitted,
            toOwner ? quest.OwnerId : null,
            toOwner ? $"<@{userId}> submitted for your review." : $"<@{userId}> submitted for review.");
        return QuestResult.Ok;
    }

    /// <summary>
    /// Send a submitted quest back to the worker to revise and resubmit (capped by guild config). For guild
    /// quests <paramref name="memberId"/> selects whose submission goes back and a quest manager is the actor
    /// (auth enforced by the caller); for personal quests it's null and <paramref name="actorId"/> must be the owner.
    /// </summary>
    public async Task<QuestResult> RequestRevisionAsync(
        Guid questId, ulong actorId, ulong? memberId, string? note, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        QuestParticipant? taker;
        if (quest.Origin == QuestOrigin.Player)
        {
            if (quest.OwnerId != actorId)
            {
                return QuestResult.Forbidden;
            }

            if (quest.Status != QuestStatus.Open)
            {
                return QuestResult.InvalidState;
            }

            taker = quest.Participants.FirstOrDefault(p => p.Status == QuestParticipantStatus.Submitted);
        }
        else
        {
            if (memberId is not { } mid)
            {
                return QuestResult.InvalidState;
            }

            taker = quest.Participants.FirstOrDefault(p => p.UserId == mid && p.Status == QuestParticipantStatus.Submitted);
        }

        if (taker is null)
        {
            return QuestResult.InvalidState;
        }

        var max = (await db.GetQuestSettingsAsync(quest.GuildId, ct)).MaxRevisions;
        if (max > 0 && taker.RevisionCount >= max)
        {
            return QuestResult.InvalidState; // out of revisions — settle it instead
        }

        taker.RequestRevision(actorId, note);
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.RevisionRequested, taker.UserId, note ?? "Please revise and resubmit.");
        return QuestResult.Ok;
    }

    /// <summary>
    /// Auto-release idle claimers on a quest whose claim has sat past <paramref name="cutoff"/> with no
    /// submission, returning the released members. The quest stays Open for another taker. Publishes a
    /// <see cref="QuestLifecycleMoment.Released"/> per member; the caller (the maintenance sweep) records the audit.
    /// </summary>
    public async Task<IReadOnlyList<ulong>> ReleaseStaleClaimsAsync(Guid questId, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return [];
        }

        var idle = quest.Participants
            .Where(p => p.Status == QuestParticipantStatus.Claimed && p.ClaimedAt < cutoff)
            .ToList();
        if (idle.Count == 0)
        {
            return [];
        }

        foreach (var p in idle)
        {
            p.Release("Auto-released: claim timed out without a submission.");
        }

        await db.SaveChangesAsync(ct);

        foreach (var p in idle)
        {
            await bus.NotifyAsync(quest, QuestLifecycleMoment.Released, p.UserId, "Claim timed out — released for another taker.");
        }

        return idle.Select(p => p.UserId).ToList();
    }

    /// <summary>
    /// Voluntarily abandon (self) or force-release (manager) one member's active participation. The slot opens up
    /// and the quest stays Open for another taker; the member may re-claim. For a player bounty the escrow stays
    /// held (the bounty is still live) — the owner cancels to refund. Authorization is the handler's job.
    /// </summary>
    public async Task<QuestResult> ReleaseClaimAsync(Guid questId, ulong targetUserId, ulong actorId, CancellationToken ct = default)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        if (quest is null)
        {
            return QuestResult.NotFound;
        }

        var participant = quest.Participants.FirstOrDefault(p => p.UserId == targetUserId
            && p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted or QuestParticipantStatus.RevisionRequested);
        if (participant is null)
        {
            return QuestResult.InvalidState; // nothing active to release
        }

        var self = actorId == targetUserId;
        participant.Release(self ? "Abandoned by the worker." : "Released by a quest manager.");
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Released, targetUserId,
            self ? "You abandoned this quest — your slot is open again." : "A quest manager released your claim.");
        return QuestResult.Ok;
    }

    // ---------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Settle a submitted bounty: pay the completer the escrowed reward plus any tier bonus POINTS, close the
    /// quest, commit, and raise the lifecycle notification. The money event itself is emitted by the currency layer.
    /// </summary>
    private async Task<QuestResult> PaySettleAsync(GuildQuest quest, QuestParticipant taker, ulong reviewerId, CancellationToken ct)
    {
        await currency.PayoutAsync(quest.GuildId, taker.UserId, quest.RewardCurrencyId, quest.EscrowAmount, Key(quest.Id), ct);
        if (quest.BonusPoints > 0)
        {
            await currency.StagePointsAsync(
                quest.GuildId, taker.UserId, quest.BonusPoints, CurrencyLedgerSource.Quest,
                $"bounty:{quest.Id}:bonus:{taker.UserId}", $"Quest bonus: {quest.Name}", ct);
        }

        taker.Approve(reviewerId);
        quest.TransitionTo(QuestStatus.Closed);
        quest.EscrowAmount = 0;
        await db.SaveChangesAsync(ct);
        await bus.NotifyAsync(quest, QuestLifecycleMoment.Settled, taker.UserId, "Paid the completer.");
        return QuestResult.Ok;
    }

    /// <summary>Refund the owner's escrow, release any active taker, and move the quest to a terminal state. Caller saves + notifies.</summary>
    private async Task RefundAndReleaseAsync(GuildQuest quest, QuestStatus terminal, CancellationToken ct)
    {
        await currency.RefundAsync(quest.GuildId, quest.OwnerId, quest.RewardCurrencyId, quest.EscrowAmount, Key(quest.Id), ct);
        ReleaseTaker(quest);
        quest.TransitionTo(terminal);
        quest.EscrowAmount = 0;
    }

    /// <summary>Load a personal-quest aggregate, or null if it doesn't exist or isn't player-funded.</summary>
    private async Task<GuildQuest?> LoadPersonalAsync(Guid questId, CancellationToken ct)
    {
        var quest = await db.FindQuestAsync(questId, ct);
        return quest?.Origin == QuestOrigin.Player ? quest : null;
    }

    private static void ReleaseTaker(GuildQuest quest)
    {
        foreach (var p in quest.Participants)
        {
            p.Release();
        }
    }
}
