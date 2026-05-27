using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot;

/// <summary>
/// Periodically flushes always-on background voice accrual for members who are still present (silent
/// sitters generate no voice events, so without this they'd only settle on leave). Reconciliation is
/// idempotent and snapshot-driven, so a missed tick self-heals on the next one. Bot-only: it reads the
/// live voice roster from the gateway cache.
/// </summary>
public class BackgroundFlushScheduler(
    IServiceScopeFactory scopeFactory, GatewayClient client, ILogger<BackgroundFlushScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Background flush scheduler started; interval {Interval}.", Interval);

        // After a restart an open accrual segment is stale (we didn't observe presence while disconnected);
        // void them so the first reconcile reopens fresh and downtime can't be credited.
        try
        {
            using var startup = scopeFactory.CreateScope();
            var background = startup.ServiceProvider.GetRequiredService<BackgroundTrackingService>();
            var voided = await background.VoidOpenSegmentsAsync(stoppingToken);
            if (voided > 0)
            {
                logger.LogInformation("Voided {Count} stale background voice segment(s) on startup.", voided);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to void stale background voice segments on startup.");
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
                var background = scope.ServiceProvider.GetRequiredService<BackgroundTrackingService>();

                foreach (var guildId in await db.ListGuildIdsWithVoiceTrackingAsync(stoppingToken))
                {
                    await background.ReconcileGuildAsync(guildId, VoiceRoster.Snapshot(client, guildId), ct: stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Background flush sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
