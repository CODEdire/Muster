using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Reconciles time-based shop state once a minute: settles orders whose buyer-confirm window has lapsed
/// (auto-settle to the seller, less the burned commission). Runs the work directly. Settlement is idempotent
/// (ledger legs keyed by order + guarded by the order's terminal status), so running on more than one node is
/// safe and a missed tick self-heals on the next one. Listing/offer expiry + dispute timeouts land in later phases.
/// </summary>
public class ShopSweepScheduler(IServiceScopeFactory scopeFactory, ILogger<ShopSweepScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>The orphan-image sweep enumerates the whole blob container, so it runs far slower than the
    /// once-a-minute time-based reconciliation.</summary>
    private static readonly TimeSpan OrphanSweepInterval = TimeSpan.FromHours(1);
    private DateTimeOffset _nextOrphanSweep = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Shop sweep scheduler started; interval {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var shop = scope.ServiceProvider.GetRequiredService<IShopService>();
                var now = DateTimeOffset.UtcNow;
                var settled = await shop.AutoSettleDueAsync(now, stoppingToken);
                var undelivered = await shop.AutoCancelUndeliveredDueAsync(now, stoppingToken);
                var resolved = await shop.AutoResolveDisputesAsync(now, stoppingToken);
                var expired = await shop.AutoExpireOffersAsync(now, stoppingToken);
                var revealed = await shop.RevealClosedRatingWindowsAsync(now, stoppingToken);
                var unfeatured = await shop.ExpireFeaturedDueAsync(now, stoppingToken);
                var delisted = await shop.ExpireListingsDueAsync(now, stoppingToken);
                if (settled > 0 || undelivered > 0 || resolved > 0 || expired > 0 || revealed > 0 || unfeatured > 0 || delisted > 0)
                {
                    logger.LogInformation(
                        "Shop sweep auto-settled {Settled} order(s), auto-cancelled {Undelivered} undelivered order(s), auto-resolved {Resolved} dispute(s), expired {Expired} offer(s), closed {Revealed} rating window(s), unfeatured {Unfeatured} listing(s), delisted {Delisted} expired listing(s).",
                        settled, undelivered, resolved, expired, revealed, unfeatured, delisted);
                }

                if (now >= _nextOrphanSweep)
                {
                    _nextOrphanSweep = now + OrphanSweepInterval;
                    var purged = await shop.SweepOrphanImagesAsync(now, stoppingToken);
                    if (purged > 0)
                    {
                        logger.LogInformation("Shop sweep purged {Purged} orphaned image blob(s).", purged);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Shop sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
