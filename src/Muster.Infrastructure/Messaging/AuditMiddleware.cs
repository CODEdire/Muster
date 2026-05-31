using Muster.Contracts;
using Muster.Infrastructure.Services.Platform;
using Wolverine;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Wolverine middleware that records the audit trail for every successful guild command — the one place
/// auditing happens, so adapters (bot/web/api) don't each re-implement it. Registered only against handler
/// chains whose message is an <see cref="IGuildCommand"/>; runs after the handler and records when the
/// command succeeded. Known command types get structured payloads + categorized actions; unknown types fall
/// through to a System.Command record so nothing's ever silently dropped.
/// </summary>
public static class AuditMiddleware
{
    /// <summary>
    /// First parameter is declared as <c>object</c> because some handlers return <c>Result&lt;T&gt;</c>
    /// (e.g. <c>PostQuestHandler</c> returns <c>Result&lt;Guid&gt;</c>) instead of the plain <c>Result</c>
    /// most return. JasperFx's codegen does exact-type matching for middleware parameters and won't bind a
    /// <c>Result&lt;Guid&gt;</c> variable to a <c>Result</c> parameter — even though it's a base type — so
    /// the parameter is <c>object</c> and we cast back to <c>Result</c> below. The downcast to
    /// <c>Result&lt;Guid&gt;</c> on the PostQuest branch still works because the runtime type is correct.
    /// </summary>
    public static async Task After(object result, Envelope envelope, AuditService audit, CancellationToken ct)
    {
        if (result is not Result r || !r.Ok || envelope.Message is not IGuildCommand command)
        {
            return;
        }

        // Targeted mappings for high-traffic commands. Adding a case here is the cheapest way to upgrade a
        // command's audit from "generic dump" to "rich formatter-friendly row" — no per-call-site churn needed.
        switch (command)
        {
            case AdjustCurrency a:
                await audit.RecordAsync(
                    a.GuildId, a.ActorId, AuditActions.Currency.Adjust,
                    new CurrencyAdjustPayload(a.CurrencyCode, a.UserId, a.Delta, a.Reason, a.ExternalId),
                    targetUserId: a.UserId, ct: ct);
                return;

            case TransferCurrency t:
                await audit.RecordAsync(
                    t.GuildId, t.ActorId, AuditActions.Currency.Transfer,
                    new CurrencyTransferPayload(t.CurrencyCode, t.FromUserId, t.ToUserId, t.Amount, t.Reason),
                    targetUserId: t.ToUserId, ct: ct);
                return;

            case MintCurrency m:
                await audit.RecordAsync(
                    m.GuildId, command.ActorId, AuditActions.Currency.Mint,
                    new CurrencyMintPayload(m.CurrencyCode, m.UserId, m.Amount, m.Reason, m.ExternalId),
                    targetUserId: m.UserId, ct: ct);
                return;

            case SpendCurrency s:
                await audit.RecordAsync(
                    s.GuildId, command.ActorId, AuditActions.Currency.Spend,
                    new CurrencySpendPayload(s.CurrencyCode, s.UserId, s.Amount, s.Reason, s.ExternalId),
                    targetUserId: s.UserId, ct: ct);
                return;

            // ---- Quest lifecycle ------------------------------------------

            case PostQuest q:
                // PostQuestHandler returns Result<Guid> with the new quest id; down-cast to read it.
                var newQuestId = (result as Result<Guid>)?.Value ?? Guid.Empty;
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.Post,
                    new QuestPostPayload(newQuestId, q.Origin, q.Name, q.CurrencyCode, q.Reward, q.Tier, q.Capacity, q.StartsAt, q.Deadline),
                    ct: ct);
                return;

            case EditQuest q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.Edit,
                    new QuestEditPayload(q.QuestId, q.Name, q.Description, q.Reward, q.Deadline, q.Tier, q.Capacity),
                    ct: ct);
                return;

            case CancelQuest q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.Cancel,
                    new QuestCancelPayload(q.QuestId), ct: ct);
                return;

            case ClaimQuest q:
                await audit.RecordAsync(
                    q.GuildId, q.UserId, AuditActions.Quests.Claim,
                    new QuestClaimPayload(q.QuestId, q.UserId),
                    targetUserId: q.UserId, ct: ct);
                return;

            case SubmitQuest q:
                await audit.RecordAsync(
                    q.GuildId, q.UserId, AuditActions.Quests.Submit,
                    new QuestSubmitPayload(q.QuestId, q.UserId, q.Note),
                    targetUserId: q.UserId, ct: ct);
                return;

            case ApproveQuestSubmission q:
                await audit.RecordAsync(
                    q.GuildId, q.ReviewerId, AuditActions.Quests.Approve,
                    new QuestReviewPayload(q.QuestId, q.MemberId, q.Note),
                    targetUserId: q.MemberId, ct: ct);
                return;

            case RejectQuestSubmission q:
                await audit.RecordAsync(
                    q.GuildId, q.ReviewerId, AuditActions.Quests.Reject,
                    new QuestReviewPayload(q.QuestId, q.MemberId, q.Note),
                    targetUserId: q.MemberId, ct: ct);
                return;

            case ReopenQuestRejection q:
                await audit.RecordAsync(
                    q.GuildId, q.ReviewerId, AuditActions.Quests.ReopenRejection,
                    new QuestReviewPayload(q.QuestId, q.MemberId, Note: null),
                    targetUserId: q.MemberId, ct: ct);
                return;

            case ConfirmQuest q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.Confirm,
                    new QuestConfirmPayload(q.QuestId), ct: ct);
                return;

            case DisputeQuest q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.Dispute,
                    new QuestDisputePayload(q.QuestId, q.Reason), ct: ct);
                return;

            case ArbitrateQuest q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.Arbitrate,
                    new QuestArbitratePayload(q.QuestId, q.Pay), ct: ct);
                return;

            case RequestQuestRevision q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.RequestRevision,
                    new QuestRevisionPayload(q.QuestId, q.MemberId, q.Note),
                    targetUserId: q.MemberId, ct: ct);
                return;

            case AcceptQuestIntake q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.AcceptIntake,
                    new QuestAcceptIntakePayload(q.QuestId, q.Tier, q.RequireFinalApproval), ct: ct);
                return;

            case RejectQuestIntake q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.RejectIntake,
                    new QuestRejectIntakePayload(q.QuestId), ct: ct);
                return;

            case FinalizeQuest q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.Finalize,
                    new QuestFinalizePayload(q.QuestId, q.Pay), ct: ct);
                return;

            case ReleaseQuestClaim q:
                await audit.RecordAsync(
                    q.GuildId, q.ActorId, AuditActions.Quests.ReleaseClaim,
                    new QuestReleaseClaimPayload(q.QuestId, q.TargetUserId),
                    targetUserId: q.TargetUserId, ct: ct);
                return;

            // ---- Muster (reaction check-in) ------------------------------

            case CreateMuster c:
                await audit.RecordAsync(c.GuildId, c.ActorId, AuditActions.Muster.Create, c.Title ?? c.Prompt, ct: ct);
                return;

            case CheckInMuster c:
                await audit.RecordAsync(c.GuildId, c.ActorId, AuditActions.Muster.CheckIn, $"muster:{c.MusterId}", targetUserId: c.ActorId, ct: ct);
                return;

            case AddMusterParticipant c:
                await audit.RecordAsync(c.GuildId, c.ActorId, AuditActions.Muster.AddParticipant, $"muster:{c.MusterId}", targetUserId: c.UserId, ct: ct);
                return;

            case RemoveMusterParticipant c:
                await audit.RecordAsync(c.GuildId, c.ActorId, AuditActions.Muster.RemoveParticipant, $"muster:{c.MusterId}", targetUserId: c.UserId, ct: ct);
                return;

            case CloseMuster c:
                await audit.RecordAsync(c.GuildId, c.ActorId, AuditActions.Muster.Close, $"muster:{c.MusterId}", ct: ct);
                return;

            case LinkMusterToSession c:
                await audit.RecordAsync(c.GuildId, c.ActorId, AuditActions.Muster.Link, $"muster:{c.MusterId} session:{c.SessionId}", ct: ct);
                return;

            case UnlinkMusterFromSession c:
                await audit.RecordAsync(c.GuildId, c.ActorId, AuditActions.Muster.Unlink, $"muster:{c.MusterId} session:{c.SessionId}", ct: ct);
                return;

            case SetSessionCoinGate c:
                await audit.RecordAsync(c.GuildId, c.ActorId, AuditActions.Muster.SetGate, $"session:{c.SessionId} gate:{c.Gate}", ct: ct);
                return;

            default:
                // Unknown command — still record it so nothing escapes the audit. ToString() of a record gives
                // readable {Field = value} output, which the default formatter renders as muted text.
                await audit.RecordAsync(
                    command.GuildId, command.ActorId,
                    AuditActions.System.Command(command.GetType().Name),
                    command.ToString(), ct: ct);
                return;
        }
    }
}
