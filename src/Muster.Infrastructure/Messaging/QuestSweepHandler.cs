using Microsoft.Extensions.Logging;
using Muster.Contracts;
using Muster.Infrastructure.Services;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Reconciles time-based quest state on a periodic <see cref="SweepDueQuests"/> tick: activates
/// scheduled quests whose start has passed and refunds/expires past-deadline personal quests. Both
/// operations are idempotent state queries over durable data, so a duplicate or missed tick is harmless —
/// the next sweep converges. Routed to a leader-pinned local queue, so only one node runs it at a time.
/// </summary>
public static class SweepDueQuestsHandler
{
    public static async Task Handle(
        SweepDueQuests command, MissionService missions, BountyService bounties,
        ILogger<SweepDueQuests> logger, CancellationToken ct)
    {
        var activated = await missions.ActivateScheduledAsync(command.Now, ct);
        var expired = await bounties.ExpireDueAsync(command.Now, ct);

        if (activated > 0 || expired > 0)
        {
            logger.LogInformation("Quest sweep activated {Activated} and expired {Expired} quests.", activated, expired);
        }
    }
}
