using Microsoft.Extensions.Configuration;
using Muster.Contracts;
using Muster.Infrastructure.Services.Quests;
using Muster.Persistence;
using NetCord.Gateway;

namespace Muster.Bot;

/// <summary>
/// Auto-pushes a private DM to the person a lifecycle moment is *about*, so they don't have to watch the board. Two
/// kinds:
/// <list type="bullet">
/// <item><b>Action cards</b> (claim/submit/revision) — the full board card + the per-user
/// <see cref="QuestComponentBuilder.DmActions"/> buttons, so they can act straight from the DM.</item>
/// <item><b>Outcome notices</b> (paid/rejected/refunded/released…) — a lightweight, button-less
/// <see cref="QuestEmbedRenderer.RenderOutcome"/> summary closing the loop on the result.</item>
/// </list>
/// Best-effort — silently skips if the user's DMs are closed (they can still use the board's "My actions"). Manager-
/// facing moments have no DM target, so they're ignored (managers act in the mod channel).
/// </summary>
public static class QuestDmPushHandler
{
    // Actionable moments → full card + buttons.
    private static readonly HashSet<QuestLifecycleMoment> ActionMoments =
    [
        QuestLifecycleMoment.Claimed,
        QuestLifecycleMoment.Submitted,
        QuestLifecycleMoment.RevisionRequested,
    ];

    // Terminal/outcome moments → lightweight notice, no buttons (nothing left for them to do).
    private static readonly HashSet<QuestLifecycleMoment> OutcomeMoments =
    [
        QuestLifecycleMoment.Settled,
        QuestLifecycleMoment.Rejected,
        QuestLifecycleMoment.RejectedAtIntake,
        QuestLifecycleMoment.Refunded,
        QuestLifecycleMoment.Released,
        QuestLifecycleMoment.Reopened,
    ];

    public static async Task Handle(
        QuestLifecycleNotified e,
        MusterDbContext db,
        IQuestReadService reads,
        GatewayClient client,
        IConfiguration config,
        CancellationToken ct)
    {
        if (e.TargetUserId is not { } target)
        {
            return;
        }

        var isAction = ActionMoments.Contains(e.Moment);
        var isOutcome = OutcomeMoments.Contains(e.Moment);
        if (!isAction && !isOutcome)
        {
            return;
        }

        var quest = await reads.GetQuestDetailAsync(e.GuildId, e.QuestId, ct);
        if (quest is null)
        {
            return;
        }

        if (isAction)
        {
            var actions = QuestComponentBuilder.DmActions(quest, e.GuildId, target);
            if (actions.Count == 0)
            {
                return; // nothing for them to do — don't spam a DM
            }

            var card = QuestEmbedRenderer.Render(quest, e.GuildId, config["Web:BaseUrl"]);
            await QuestDm.TrySendAsync(client, target, card, actions); // best-effort; DMs-closed → no-op
            return;
        }

        // Outcome notice — closes the loop, no buttons.
        var notice = QuestEmbedRenderer.RenderOutcome(quest, e.GuildId, e.Moment, config["Web:BaseUrl"]);
        await QuestDm.TrySendAsync(client, target, notice, []);
    }
}
