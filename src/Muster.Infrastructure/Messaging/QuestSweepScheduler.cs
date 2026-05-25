using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Contracts;
using Wolverine;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Publishes a <see cref="SweepDueQuests"/> tick on a fixed interval. Every node running this may publish,
/// but the tick is routed to a leader-pinned queue (see <see cref="WolverineExtensions"/>), so the work
/// runs on a single node and is idempotent regardless. Hosting this on more than one node only adds
/// cheap, no-op duplicate ticks — it never double-applies the work.
/// </summary>
public class QuestSweepScheduler(IServiceScopeFactory scopeFactory, ILogger<QuestSweepScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
                await bus.PublishAsync(new SweepDueQuests(DateTimeOffset.UtcNow));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to publish quest sweep tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
