using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Infrastructure.Services.Currencies;

namespace Muster.Bot;

/// <summary>
/// Daily sweep that compacts ledger history per guild's <c>LedgerRetentionDays</c> setting: rows beyond the window
/// are folded into one carry-forward <c>Checkpoint</c> entry per (user, currency, season), preserving balances.
/// A no-op for guilds with retention 0 (full history). Runs in the Bot host as a single periodic worker.
/// </summary>
public class LedgerPruneScheduler(
    IServiceScopeFactory scopeFactory, ILogger<LedgerPruneScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ledger prune sweep started; interval {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var prune = scope.ServiceProvider.GetRequiredService<ILedgerPruneService>();
                var pruned = await prune.PruneAllAsync(stoppingToken);
                if (pruned > 0)
                {
                    logger.LogInformation("Ledger prune sweep compacted {Rows} row(s).", pruned);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ledger prune sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
