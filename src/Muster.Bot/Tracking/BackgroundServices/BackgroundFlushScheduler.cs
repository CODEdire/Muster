using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Bot.Platform.Telemetry;
using NetCord.Gateway;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot.Tracking.BackgroundServices;

/// <summary>
/// Periodically flushes voice accrual for members who are still present (silent sitters generate no voice
/// events, so without this they'd only settle on leave) — both the always-on background plane and active
/// bounded sessions. Reconciliation is idempotent and snapshot-driven, so a missed tick self-heals on the
/// next one. Bot-only: it reads the live voice roster from the gateway cache.
/// </summary>
public class BackgroundFlushScheduler(
    IServiceScopeFactory scopeFactory, GuildReconcileCoordinator coordinator, ILogger<BackgroundFlushScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Voice flush scheduler started; interval {Interval}.", Interval);

        // After a restart an open accrual segment is stale (we didn't observe presence while disconnected);
        // void them so the first reconcile reopens fresh and downtime can't be credited.
        try
        {
            using var startup = scopeFactory.CreateScope();
            var voidedBackground = await startup.ServiceProvider.GetRequiredService<BackgroundTrackingService>().VoidOpenSegmentsAsync(stoppingToken);
            var voidedSessions = await startup.ServiceProvider.GetRequiredService<TrackingSessionService>().VoidOpenAttendanceAsync(stoppingToken);
            if (voidedBackground > 0 || voidedSessions > 0)
            {
                logger.LogInformation(
                    "Voided {Background} background + {Sessions} session voice segment(s) on startup.", voidedBackground, voidedSessions);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to void stale voice segments on startup.");
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            using var tickActivity = BotTelemetry.StartBackgroundTick(nameof(BackgroundFlushScheduler));
            try
            {
                List<ulong> guildIds;
                int staleClosed;
                using (var scope = scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

                    // Visit every guild that has either monitored voice channels or an active session.
                    guildIds = (await db.ListGuildIdsWithVoiceTrackingAsync(stoppingToken))
                        .Union(await db.ListGuildIdsWithActiveSessionsAsync(stoppingToken))
                        .ToList();

                    // Safety net: close any session that's run past its guild's MaxSessionHours.
                    staleClosed = await scope.ServiceProvider.GetRequiredService<TrackingSessionService>()
                        .CloseStaleSessionsAsync(ct: stoppingToken);
                }

                tickActivity?.SetTag("guilds.count", guildIds.Count);

                // Reconcile through the coordinator so the sweep can't race a debounced run for the same guild.
                foreach (var guildId in guildIds)
                {
                    await coordinator.ReconcileNowAsync(guildId, stoppingToken);
                }

                tickActivity.SetResult(ok: true);

                if (staleClosed > 0)
                {
                    logger.LogInformation("Auto-closed {Count} stale session(s) past their max duration.", staleClosed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tickActivity.SetException(ex);
                logger.LogError(ex, "Voice flush sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
