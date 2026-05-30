using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot.Tracking.BackgroundServices;

/// <summary>
/// Reconciles voice accrual exactly at reward-multiplier window edges. Accrual normally flushes on voice
/// events + the ~5-minute sweep, so a segment spanning a multiplier boundary would be credited at a single
/// regime. By flushing at each boundary, every segment stays within one regime and is credited (at its
/// start-time factor) exactly. Idempotent — a missed edge self-heals on the next sweep. Bot-only (reads the
/// live roster via the coordinator).
/// </summary>
public class MultiplierBoundaryScheduler(
    IServiceScopeFactory scopeFactory, GuildReconcileCoordinator coordinator, ILogger<MultiplierBoundaryScheduler> logger)
    : BackgroundService
{
    // Re-scan at least this often so newly-added multipliers / guilds are picked up even if the next edge is distant.
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Multiplier boundary scheduler started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var boundaries = await ComputeBoundariesAsync(now, stoppingToken);

                var soonest = boundaries.Count > 0 ? boundaries.Min(b => b.Boundary) : now + MaxWait;
                var wait = soonest - DateTimeOffset.UtcNow;
                if (wait < MinWait) wait = MinWait;
                if (wait > MaxWait) wait = MaxWait;

                await Task.Delay(wait, stoppingToken);

                // Reconcile the guilds whose boundary has now arrived (flush the pre-edge segment, reopen post-edge).
                var fired = DateTimeOffset.UtcNow + Tolerance;
                var due = boundaries.Where(b => b.Boundary <= fired).ToList();
                if (due.Count == 0)
                {
                    continue;
                }

                using var tickActivity = BotTelemetry.StartBackgroundTick(nameof(MultiplierBoundaryScheduler));
                tickActivity?.SetTag("guilds.count", due.Count);
                try
                {
                    foreach (var (guildId, _) in due)
                    {
                        await coordinator.ReconcileNowAsync(guildId, stoppingToken);
                    }
                    tickActivity.SetResult(ok: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    tickActivity.SetException(ex);
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Multiplier boundary scheduler iteration failed.");
                await Task.Delay(MaxWait, stoppingToken);
            }
        }
    }

    private async Task<List<(ulong GuildId, DateTimeOffset Boundary)>> ComputeBoundariesAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var multipliers = scope.ServiceProvider.GetRequiredService<RewardMultiplierService>();

        // Only guilds with active accrual (monitored voice or an open session) have anything to flush at an edge.
        var guildIds = (await db.ListGuildIdsWithVoiceTrackingAsync(ct))
            .Union(await db.ListGuildIdsWithActiveSessionsAsync(ct))
            .ToList();

        var result = new List<(ulong, DateTimeOffset)>();
        foreach (var guildId in guildIds)
        {
            var set = await multipliers.LoadAsync(guildId, ct);
            if (!set.IsEmpty && set.NextBoundaryAfter(now) is { } boundary)
            {
                result.Add((guildId, boundary));
            }
        }

        return result;
    }
}
