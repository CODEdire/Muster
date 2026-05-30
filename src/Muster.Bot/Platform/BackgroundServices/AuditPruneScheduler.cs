using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Bot.Platform.Telemetry;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Bot.Platform.BackgroundServices;

/// <summary>
/// Daily sweep that deletes audit rows older than the configured <c>Audit:RetentionDays</c> (default 90; 0 disables).
/// Runs in the Bot host as a single periodic worker — same shape as <see cref="LedgerPruneScheduler"/>.
/// </summary>
public class AuditPruneScheduler(
    IServiceScopeFactory scopeFactory, ILogger<AuditPruneScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Audit prune sweep started; interval {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            using var activity = BotTelemetry.StartBackgroundTick(nameof(AuditPruneScheduler));
            try
            {
                using var scope = scopeFactory.CreateScope();
                var prune = scope.ServiceProvider.GetRequiredService<IAuditPruneService>();
                await prune.PruneAsync(stoppingToken);
                activity.SetResult(ok: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity.SetException(ex);
                logger.LogError(ex, "Audit prune sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
