using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Infrastructure.Services;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Reconciles time-based quest state once a minute: activates scheduled quests whose start has passed
/// and refunds/expires past-deadline personal quests. Runs the work directly (no message hop) so it
/// executes deterministically. Both operations are idempotent state queries over durable data — guarded
/// status flips and escrow refunds keyed by a deterministic ledger source — so running it on more than
/// one node is safe; a missed tick simply self-heals on the next one.
/// </summary>
public class QuestSweepScheduler(IServiceScopeFactory scopeFactory, ILogger<QuestSweepScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Quest sweep scheduler started; interval {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var missions = scope.ServiceProvider.GetRequiredService<MissionService>();
                var bounties = scope.ServiceProvider.GetRequiredService<BountyService>();

                var now = DateTimeOffset.UtcNow;
                var activated = await missions.ActivateScheduledAsync(now, stoppingToken);
                var expired = await bounties.ExpireDueAsync(now, stoppingToken)       // personal quests (refund escrow)
                    + await missions.ExpireDueQuestsAsync(now, stoppingToken);        // guild quests (close, nothing to refund)

                if (activated > 0 || expired > 0)
                {
                    logger.LogInformation("Quest sweep activated {Activated} and expired {Expired} quests.", activated, expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Quest sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
