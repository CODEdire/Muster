using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot;

/// <summary>
/// Daily sweep that prunes raw <c>ActivityRecord</c> rows beyond each guild's retention window (rollups are
/// kept, so stats survive). Idempotent and cheap when retention is off (the default). Safe on multiple nodes.
/// </summary>
public class ActivityPruneScheduler(IServiceScopeFactory scopeFactory, ILogger<ActivityPruneScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var pruned = await scope.ServiceProvider.GetRequiredService<ActivityService>().PruneOldRecordsAsync(ct: stoppingToken);
                if (pruned > 0)
                {
                    logger.LogInformation("Pruned {Count} stale activity record(s).", pruned);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Activity prune sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
