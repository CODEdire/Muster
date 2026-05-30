using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Bot.Platform.Telemetry;
using Muster.Infrastructure.Services.Currencies;

namespace Muster.Bot.Economy.BackgroundServices;

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
            using var activity = BotTelemetry.StartBackgroundTick(nameof(LedgerPruneScheduler));
            try
            {
                using var scope = scopeFactory.CreateScope();
                var prune = scope.ServiceProvider.GetRequiredService<ILedgerPruneService>();
                var pruned = await prune.PruneAllAsync(stoppingToken);
                activity?.SetTag("items.processed", pruned);
                activity.SetResult(ok: true);
                if (pruned > 0)
                {
                    logger.LogInformation("Ledger prune sweep compacted {Rows} row(s).", pruned);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity.SetException(ex);
                logger.LogError(ex, "Ledger prune sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
