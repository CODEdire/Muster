using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot;

/// <summary>
/// Periodically flushes voice accrual for members who are still present (silent sitters generate no voice
/// events, so without this they'd only settle on leave) — both the always-on background plane and active
/// bounded sessions. Reconciliation is idempotent and snapshot-driven, so a missed tick self-heals on the
/// next one. Bot-only: it reads the live voice roster from the gateway cache.
/// </summary>
public class BackgroundFlushScheduler(
    IServiceScopeFactory scopeFactory, GatewayClient client, ILogger<BackgroundFlushScheduler> logger)
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
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
                var background = scope.ServiceProvider.GetRequiredService<BackgroundTrackingService>();
                var sessions = scope.ServiceProvider.GetRequiredService<TrackingSessionService>();

                // Visit every guild that has either monitored voice channels or an active session.
                var guildIds = (await db.ListGuildIdsWithVoiceTrackingAsync(stoppingToken))
                    .Union(await db.ListGuildIdsWithActiveSessionsAsync(stoppingToken));

                foreach (var guildId in guildIds)
                {
                    var roster = VoiceRoster.Snapshot(client, guildId);
                    await sessions.ReconcileSessionsAsync(guildId, roster, ct: stoppingToken);
                    await background.ReconcileGuildAsync(guildId, roster, ct: stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Voice flush sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
