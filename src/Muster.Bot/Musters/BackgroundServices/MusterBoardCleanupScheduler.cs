using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord.Gateway;
using NetCord.Rest;

namespace Muster.Bot.Musters.BackgroundServices;

/// <summary>
/// Prunes terminal muster cards from the channel after the guild's retention window. A closed/expired/cancelled
/// muster's card lingers for <c>MusterSettings.BoardRetentionHours</c>, then this deletes the Discord message and
/// drops the <c>PostedMessage</c> link — the muster, its roster, and the ledger stay in the DB (web keeps full
/// history; only the channel message is ephemeral). Best-effort + idempotent (mirrors the quest-board cleanup).
/// </summary>
public class MusterBoardCleanupScheduler(
    IServiceScopeFactory scopeFactory,
    GatewayClient client,
    ILogger<MusterBoardCleanupScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>A terminal card is due for deletion once its retention window has elapsed (0 = delete immediately).</summary>
    public static bool IsExpired(DateTimeOffset terminalAt, int retentionHours, DateTimeOffset now)
        => now >= terminalAt.AddHours(Math.Max(0, retentionHours));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Muster board cleanup started; interval {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            using var activity = BotTelemetry.StartBackgroundTick(nameof(MusterBoardCleanupScheduler));
            try
            {
                await SweepAsync(stoppingToken);
                activity.SetResult(ok: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity.SetException(ex);
                logger.LogError(ex, "Muster board cleanup failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

        var cards = await db.ListTerminalMusterBoardCardsAsync(ct);
        if (cards.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var removed = 0;

        foreach (var card in cards)
        {
            // Retention is the muster's own snapshot (template/default at creation).
            if (!IsExpired(card.TerminalAt, card.RetentionHours, now))
            {
                continue;
            }

            try
            {
                await client.Rest.DeleteMessageAsync(card.ChannelId, card.MessageId, cancellationToken: ct);
            }
            catch (RestException ex)
            {
                logger.LogWarning(ex, "Muster card {MessageId} could not be deleted (already gone?).", card.MessageId);
            }

            await db.PostedMessages.Where(p => p.Id == card.PostedMessageId).ExecuteDeleteAsync(ct);
            removed++;
        }

        if (removed > 0)
        {
            logger.LogInformation("Muster board cleanup removed {Count} terminal card(s).", removed);
        }
    }
}
