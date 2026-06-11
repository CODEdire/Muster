using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;

namespace Muster.Web.Components.Pages.Quests.Core;

/// <summary>Pure presentation helpers for the quest board — shared by the board cards, the table view, and the
/// page. No instance state; data-dependent display (names, avatars, types, deadlines) lives on
/// <see cref="QuestCardData"/>.</summary>
public static class QuestPresentation
{
    public static bool HasActiveWorker(QuestBoardItem q) =>
        q.Participants.Any(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted
            or QuestParticipantStatus.RevisionRequested);

    public static int ClaimedCount(QuestBoardItem q) =>
        q.Participants.Count(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted
            or QuestParticipantStatus.RevisionRequested or QuestParticipantStatus.Approved);

    /// <summary>Participants currently on the quest (claimed/submitted/in-revision/approved) for the avatar stack.</summary>
    public static IReadOnlyList<QuestParticipantView> ActiveWorkers(QuestBoardItem q) =>
        q.Participants
            .Where(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted
                or QuestParticipantStatus.RevisionRequested or QuestParticipantStatus.Approved)
            .ToList();

    /// <summary>Everyone with a real outcome on the quest (excludes Released give-ups), for the roster popup.</summary>
    public static IReadOnlyList<QuestParticipantView> RosterParticipants(QuestBoardItem q) =>
        q.Participants
            .Where(p => p.Status != QuestParticipantStatus.Released)
            .OrderByDescending(p => p.Status == QuestParticipantStatus.Approved)
            .ToList();

    /// <summary>Avatar ring colour by participant status.</summary>
    public static string RingClass(QuestParticipantStatus s) => s switch
    {
        QuestParticipantStatus.Approved => "approved",
        QuestParticipantStatus.Submitted => "submitted",
        QuestParticipantStatus.RevisionRequested => "revision",
        QuestParticipantStatus.Rejected => "rejected",
        _ => "claimed",
    };

    public static string ParticipantChip(QuestParticipantStatus s) => s switch
    {
        QuestParticipantStatus.Approved => "chip-review",
        QuestParticipantStatus.Rejected => "chip-closed",
        _ => "chip-progress",
    };

    public static string ParticipantLabel(QuestParticipantStatus s) => s switch
    {
        QuestParticipantStatus.RevisionRequested => "Revision",
        _ => s.ToString(),
    };

    public static string RewardTextClass(string code) =>
        code.Equals("POINTS", StringComparison.OrdinalIgnoreCase) ? "reward-points" : "reward-coin";

    /// <summary>An optional status chip; plain Open quests show none (the category chip carries it).</summary>
    public static (string Text, string Class)? StatusChip(QuestBoardItem q, bool isManager)
    {
        var submitted = q.Participants.Count(p => p.Status == QuestParticipantStatus.Submitted);
        if (q.Status == QuestStatus.Open && q.Origin == QuestOrigin.Guild && isManager && submitted > 0)
        {
            return ($"{submitted} to review", "chip-review");
        }

        return q.Status switch
        {
            QuestStatus.Closed => ("Closed", "chip-closed"),
            QuestStatus.Cancelled => ("Cancelled", "chip-closed"),
            QuestStatus.Expired => ("Expired", "chip-closed"),
            QuestStatus.Disputed => ("Disputed", "chip-closed"),
            QuestStatus.Scheduled => ("Scheduled", "chip-progress"),
            QuestStatus.PendingApproval => ("Pending intake", "chip-progress"),
            QuestStatus.PendingFinal => ("Awaiting sign-off", "chip-progress"),
            QuestStatus.Open when HasActiveWorker(q) => ("In progress", "chip-progress"),
            _ => null,
        };
    }
}
