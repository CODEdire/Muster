using Muster.Contracts;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Quests;
using Wolverine;

namespace Muster.Web;

/// <summary>
/// Maps a UI action token to its CQRS command and dispatches it on the bus. Shared by the quest board card
/// and the detail page so both surfaces stay in lock-step. Per-member actions carry a member id suffix
/// (e.g. "approve:123"); the rest are bare tokens.
/// </summary>
public static class QuestActionRunner
{
    public static async Task<CommandResult> RunAsync(
        IMessageBus bus, ulong guildId, Guid questId, ulong userId,
        string action, string? note = null, QuestTier tier = QuestTier.None, bool requireFinal = false)
    {
        async Task<CommandResult> D(object command, string ok) => (await bus.InvokeAsync<Result>(command)).ToCommandResult(ok);

        if (action.StartsWith("approve:", StringComparison.Ordinal) && ulong.TryParse(action[8..], out var approveId))
        {
            return await D(new ApproveQuestSubmission(guildId, questId, approveId, userId, note), "Approved and awarded the reward.");
        }

        if (action.StartsWith("reject:", StringComparison.Ordinal) && ulong.TryParse(action[7..], out var rejectId))
        {
            return await D(new RejectQuestSubmission(guildId, questId, rejectId, userId, note), "Submission rejected.");
        }

        if (action.StartsWith("reopen:", StringComparison.Ordinal) && ulong.TryParse(action[7..], out var reopenId))
        {
            return await D(new ReopenQuestRejection(guildId, questId, reopenId, userId), "Reopened the submission for review.");
        }

        if (action.StartsWith("revise:", StringComparison.Ordinal) && ulong.TryParse(action[7..], out var reviseId))
        {
            return await D(new RequestQuestRevision(guildId, questId, userId, reviseId, note), "Sent back to revise and resubmit.");
        }

        // Manager force-release of a specific member's claim ("release:123").
        if (action.StartsWith("release:", StringComparison.Ordinal) && ulong.TryParse(action[8..], out var releaseId))
        {
            return await D(new ReleaseQuestClaim(guildId, questId, userId, releaseId), "Released the member's claim — the slot is open again.");
        }

        if (action == "accept")
        {
            return await D(new AcceptQuestIntake(guildId, questId, userId, tier, requireFinal), "Accepted and opened for takers.");
        }

        return action switch
        {
            "submit" => await D(new SubmitQuest(guildId, questId, userId, note), "Quest submitted for review."),
            "abandon" => await D(new ReleaseQuestClaim(guildId, questId, userId, userId), "You abandoned this quest — the slot is open again."),
            "confirm" => await D(new ConfirmQuest(guildId, questId, userId), "Confirmed — the completer is paid (or sent for final sign-off)."),
            "cancel" => await D(new CancelQuest(guildId, questId, userId), "Quest cancelled."),
            "dispute" => await D(new DisputeQuest(guildId, questId, userId), "Dispute raised — a manager will review it."),
            "revise" => await D(new RequestQuestRevision(guildId, questId, userId, null, note), "Sent back to revise and resubmit."),
            "reject-intake" => await D(new RejectQuestIntake(guildId, questId, userId), "Rejected — the owner's escrow was refunded."),
            "finalize-pay" => await D(new FinalizeQuest(guildId, questId, userId, true), "Finalized — paid the completer."),
            "finalize-refund" => await D(new FinalizeQuest(guildId, questId, userId, false), "Finalized — refunded the owner."),
            "resolve-pay" => await D(new ArbitrateQuest(guildId, questId, userId, true), "Resolved: paid the completer."),
            "resolve-refund" => await D(new ArbitrateQuest(guildId, questId, userId, false), "Resolved: refunded the owner."),
            _ => await D(new ClaimQuest(guildId, questId, userId), "Quest claimed."),
        };
    }
}
