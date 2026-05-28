using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Bot;

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
            try
            {
                using var scope = scopeFactory.CreateScope();
                var prune = scope.ServiceProvider.GetRequiredService<IAuditPruneService>();
                await prune.PruneAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Audit prune sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
