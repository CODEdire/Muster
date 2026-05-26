using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;
using NetCord;
using NetCord.Rest;

namespace Muster.Bot;

/// <summary>
/// Builds the action components (buttons / select menus) that sit under a quest's board card, by phase and audience.
/// Pure (no Discord/IO) so it's unit-testable. The components offered are phase-appropriate for *everyone* who can
/// see the card; <b>authorization happens on click</b> (the command funnel authorizes the clicker), so a button a
/// user isn't allowed to use just yields an ephemeral "you can't do that".
///
/// custom_id scheme: <c>{prefix}:{guildId}:{questId}[:{memberId}]</c> — carries its own state so it works even from
/// a DM (no guild context). See <see cref="QuestInteractionModule"/> for the matching handlers.
/// </summary>
public static class QuestComponentBuilder
{
    // Button custom_id prefixes (also the [ComponentInteraction] route names).
    public const string Claim = "qclaim", Submit = "qsubmit", Confirm = "qconfirm", Dispute = "qdispute", Cancel = "qcancel", Abandon = "qabandon";
    public const string Approve = "qapprove", Reject = "qreject", Revise = "qrevise", ReviseMember = "qrevm";
    public const string IntakeReject = "qintreject", ArbPay = "qarbpay", ArbRefund = "qarbref", FinPay = "qfinpay", FinRefund = "qfinref";
    public const string ReviewPick = "qreview"; // submitter select (multi-review)
    public const string IntakeTier = "qinttier"; // tier select (accept intake at tier)
    public const string Mine = "qmine"; // public-card button: DM me my actions

    // Modal prefixes (button opens these; the matching modal handler dispatches with the typed note).
    public const string SubmitModal = "qsubmitm", RejectModal = "qrejm", ReviseMemberModal = "qrvm", ReviseOwnerModal = "qrvo", DisputeModal = "qdism";
    public const string NoteInput = "note"; // the modals' text-input id

    /// <summary>The modal the Submit button opens — an optional free-text note for the reviewer.</summary>
    public static ModalProperties BuildSubmitModal(ulong guildId, Guid questId) =>
        NoteModal(SubmitModal, guildId, questId, null, "Submit quest", "Note for the reviewer (optional)", "What you did…");

    /// <summary>A modal with a single optional free-text note, carrying the action's ids in its custom_id.</summary>
    public static ModalProperties NoteModal(string modalPrefix, ulong guildId, Guid questId, ulong? memberId, string title, string label, string placeholder) =>
        new(Id(modalPrefix, guildId, questId, memberId), title, new IModalComponentProperties[]
        {
            new LabelProperties(label,
                new TextInputProperties(NoteInput, TextInputStyle.Paragraph) { Required = false, MaxLength = 1000, Placeholder = placeholder }),
        });

    /// <summary>The action buttons for one recipient's private DM card — only the owner/claimer actions that apply to
    /// them right now (manager actions live in the mod channel, not DMs). Empty = they have nothing to do.</summary>
    public static IReadOnlyList<IMessageComponentProperties> DmActions(QuestDetailView q, ulong guildId, ulong recipientId)
    {
        var rows = new List<IMessageComponentProperties>();
        var mine = q.Participants.FirstOrDefault(p => p.UserId == recipientId);
        var hasSubmission = q.Participants.Any(p => p.Status == QuestParticipantStatus.Submitted);

        // Claimer: submit / resubmit, or abandon the slot.
        if (q.Status == QuestStatus.Open && mine is { Status: QuestParticipantStatus.Claimed or QuestParticipantStatus.RevisionRequested })
        {
            rows.Add(new ActionRowProperties(new ButtonProperties[]
            {
                new(Id(Submit, guildId, q.Id), "Submit", ButtonStyle.Primary),
                new(Id(Abandon, guildId, q.Id), "Abandon", ButtonStyle.Danger),
            }));
        }

        // Player-quest owner: decide a submission, or cancel before work.
        if (q.Origin == QuestOrigin.Player && q.OwnerId == recipientId)
        {
            if (q.Status == QuestStatus.Open && hasSubmission)
            {
                rows.Add(new ActionRowProperties(new ButtonProperties[]
                {
                    new(Id(Confirm, guildId, q.Id), "Confirm & pay", ButtonStyle.Success),
                    new(Id(Revise, guildId, q.Id), "Request revision", ButtonStyle.Secondary),
                    new(Id(Dispute, guildId, q.Id), "Dispute", ButtonStyle.Danger),
                }));
            }
            else if (q.Status is QuestStatus.Open or QuestStatus.Scheduled or QuestStatus.PendingApproval)
            {
                rows.Add(new ActionRowProperties(new[] { new ButtonProperties(Id(Cancel, guildId, q.Id), "Cancel", ButtonStyle.Danger) }));
            }
        }

        return rows;
    }

    public static string Id(string prefix, ulong guildId, Guid questId, ulong? memberId = null) =>
        memberId is { } m ? $"{prefix}:{guildId}:{questId}:{m}" : $"{prefix}:{guildId}:{questId}";

    public static IReadOnlyList<IMessageComponentProperties> Build(QuestDetailView q, ulong guildId, bool isMod)
    {
        var rows = new List<IMessageComponentProperties>();
        var submitters = q.Participants.Where(p => p.Status == QuestParticipantStatus.Submitted).ToList();

        if (isMod)
        {
            BuildModRows(q, guildId, submitters, rows);
        }
        else
        {
            BuildPublicRows(q, guildId, submitters, rows);
        }

        return rows;
    }

    private static void BuildPublicRows(QuestDetailView q, ulong guildId, List<QuestDetailParticipant> submitters, List<IMessageComponentProperties> rows)
    {
        if (q.Status != QuestStatus.Open)
        {
            return; // terminal / scheduled cards carry no actions on the public board
        }

        // Surface-split: the public card shows Claim (open to anyone — auth on click) and a "My actions" button that
        // privately DMs the clicker the actions that apply to *them* (Submit for claimers, owner controls for owners).
        // A shared channel message can't hide buttons per viewer, so per-user actions are delivered by DM instead.
        var active = q.Participants.Count(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted
            or QuestParticipantStatus.RevisionRequested or QuestParticipantStatus.Approved);

        var buttons = new List<ButtonProperties>();
        if (active < q.Capacity) // all slots taken → no Claim (existing workers still use My actions to submit)
        {
            buttons.Add(new(Id(Claim, guildId, q.Id), "Claim", ButtonStyle.Primary));
        }

        buttons.Add(new(Id(Mine, guildId, q.Id), "📨 My actions", ButtonStyle.Secondary));
        rows.Add(new ActionRowProperties(buttons));
    }

    private static void BuildModRows(QuestDetailView q, ulong guildId, List<QuestDetailParticipant> submitters, List<IMessageComponentProperties> rows)
    {
        switch (q.Status)
        {
            case QuestStatus.PendingApproval:
                // Accept by choosing a tier; reject refunds the owner. (A select menu is its own top-level row.)
                rows.Add(TierMenu(guildId, q.Id));
                rows.Add(new ActionRowProperties(new[] { new ButtonProperties(Id(IntakeReject, guildId, q.Id), "Reject intake (refund)", ButtonStyle.Danger) }));
                break;

            case QuestStatus.Disputed:
                rows.Add(new ActionRowProperties(new ButtonProperties[]
                {
                    new(Id(ArbPay, guildId, q.Id), "Pay completer", ButtonStyle.Success),
                    new(Id(ArbRefund, guildId, q.Id), "Refund owner", ButtonStyle.Secondary),
                }));
                break;

            case QuestStatus.PendingFinal:
                rows.Add(new ActionRowProperties(new ButtonProperties[]
                {
                    new(Id(FinPay, guildId, q.Id), "Pay completer", ButtonStyle.Success),
                    new(Id(FinRefund, guildId, q.Id), "Refund owner", ButtonStyle.Secondary),
                }));
                break;

            case QuestStatus.Open when submitters.Count == 1:
                rows.Add(ReviewButtons(guildId, q.Id, submitters[0].UserId));
                break;

            case QuestStatus.Open when submitters.Count > 1:
                // Pick whose submission to review, then act (the select's handler posts an ephemeral with the buttons).
                var options = submitters.Take(25)
                    .Select(p => new StringMenuSelectOptionProperties(p.Name, p.UserId.ToString()));
                rows.Add(new StringMenuProperties(Id(ReviewPick, guildId, q.Id), options) { Placeholder = "Review a submission…" });
                break;
        }
    }

    /// <summary>Approve / Reject / Revise buttons targeting one submitter — used on the card (single) and the
    /// ephemeral that follows the submitter select (multi).</summary>
    public static ActionRowProperties ReviewButtons(ulong guildId, Guid questId, ulong memberId) =>
        new(new ButtonProperties[]
        {
            new(Id(Approve, guildId, questId, memberId), "Approve", ButtonStyle.Success),
            new(Id(Reject, guildId, questId, memberId), "Reject", ButtonStyle.Danger),
            new(Id(ReviseMember, guildId, questId, memberId), "Request revision", ButtonStyle.Secondary),
        });

    private static StringMenuProperties TierMenu(ulong guildId, Guid questId)
    {
        var tiers = new[] { QuestTier.None, QuestTier.E, QuestTier.D, QuestTier.C, QuestTier.B, QuestTier.A, QuestTier.S };
        var options = tiers.Select(t => new StringMenuSelectOptionProperties(
            t == QuestTier.None ? "Accept · no tier" : $"Accept · tier {t}", t.ToString()));
        return new StringMenuProperties(Id(IntakeTier, guildId, questId), options) { Placeholder = "Accept & set difficulty tier…" };
    }
}
