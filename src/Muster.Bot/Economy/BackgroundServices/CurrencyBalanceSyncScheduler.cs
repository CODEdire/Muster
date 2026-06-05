using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Bot.Platform.Telemetry;
using Muster.Domain.Enums;
using Muster.Infrastructure.Connectors;
using Muster.Infrastructure.Services.Platform;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Bot.Economy.BackgroundServices;

/// <summary>
/// Periodically reconciles member balances for External/Hybrid currencies whose connector sets a sync interval.
/// For each such currency it finds wallets not synced within the interval and reconciles them, paced so the
/// external API isn't hammered. Runs in the Bot host (a single periodic worker); the dashboard's on-visit sync and
/// the admin "sync all" cover on-demand needs.
/// </summary>
public class CurrencyBalanceSyncScheduler(
    IServiceScopeFactory scopeFactory, ILogger<CurrencyBalanceSyncScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PerMemberDelay = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Currency balance sweep started; interval {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            using var activity = BotTelemetry.StartBackgroundTick(nameof(CurrencyBalanceSyncScheduler));
            try
            {
                await SweepAsync(stoppingToken);
                activity.SetResult(ok: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity.SetException(ex);
                logger.LogError(ex, "Currency balance sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<CurrencyConnectorSyncService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var now = DateTimeOffset.UtcNow;

        foreach (var guild in await db.ListActiveGuildsAsync(ct))
        {
            foreach (var currency in await db.ListCurrenciesAsync(guild.Id, ct))
            {
                if (currency.Mode == CurrencyMode.Internal)
                {
                    continue;
                }

                var cfg = currency.Connector.Normalize();
                if (!cfg.Enabled || !cfg.GetBalance.Enabled || cfg.SyncIntervalMinutes <= 0)
                {
                    continue;
                }

                var cutoff = now - TimeSpan.FromMinutes(cfg.SyncIntervalMinutes);
                var due = await db.ListWalletsNeedingSyncAsync(guild.Id, currency.Id, cutoff, ct);
                if (due.Count > 0)
                {
                    var stats = await sync.SyncCurrencyAsync(guild.Id, currency.Id, PerMemberDelay, due, ct);

                    // One summary row per currency per run (system actor) — per-member drift stays ledger-only.
                    await audit.RecordAsync(
                        guild.Id, 0, AuditActions.Currency.SyncSweep,
                        new CurrencySyncSweepPayload(currency.Code, stats.Candidates, stats.Synced, stats.Failed),
                        origin: AuditOrigin.Connector, ct: ct);
                }
            }
        }
    }
}
