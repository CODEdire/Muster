using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Bot.Platform.Telemetry;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Musters;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Wolverine;

namespace Muster.Bot.Musters.BackgroundServices;

/// <summary>
/// Auto-expires open, non-linked musters once they pass their <c>ExpiresAt</c>. Without this an expired-but-unclicked
/// muster would stay <c>Open</c> forever with a live-looking Check-In button (expiry was otherwise only evaluated
/// lazily on the next click). Expiring pays the muster's reward to its roster (via <see cref="MusterService.CloseAsync"/>,
/// which is idempotent) and publishes <see cref="MusterChanged"/> so the card goes terminal and the cleanup sweep can
/// later prune it. Linked musters are excluded — they close with their session.
/// </summary>
public class MusterExpirySweepScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<MusterExpirySweepScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Muster expiry sweep started; interval {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            using var activity = BotTelemetry.StartBackgroundTick(nameof(MusterExpirySweepScheduler));
            try
            {
                await SweepAsync(stoppingToken);
                activity.SetResult(ok: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity.SetException(ex);
                logger.LogError(ex, "Muster expiry sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

        var now = DateTimeOffset.UtcNow;
        var due = await db.ListDueExpiredMustersAsync(now, ct);
        var lockDue = await db.ListDueLockMustersAsync(now, ct);
        if (due.Count == 0 && lockDue.Count == 0)
        {
            return;
        }

        var musters = scope.ServiceProvider.GetRequiredService<MusterService>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // Standalone musters expire + pay out now.
        foreach (var m in due)
        {
            if (await musters.CloseAsync(m.Id, MusterStatus.Expired, ct))
            {
                await bus.PublishAsync(new MusterChanged(m.GuildId, m.Id, MusterChangeKind.Closed));
            }
        }

        // Linked musters soft-close (Locked): the card stays (button disabled); the session close pays + closes them.
        foreach (var m in lockDue)
        {
            if (await musters.LockAsync(m.Id, ct))
            {
                await bus.PublishAsync(new MusterChanged(m.GuildId, m.Id, MusterChangeKind.Closed));
            }
        }

        logger.LogInformation("Muster expiry sweep: expired {Expired}, locked {Locked} muster(s).", due.Count, lockDue.Count);
    }
}
