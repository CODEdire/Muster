using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Bot.Platform.Telemetry;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord.Gateway;
using NetCord.Rest;

namespace Muster.Bot.Questing.BackgroundServices;

/// <summary>
/// Prunes completed quest cards from the channel board so they don't linger forever. A quest's card stays live
/// through its terminal state (Closed/Cancelled/Expired) for the guild's retention window, then this deletes the
/// Discord message and drops the <c>PostedMessage</c> link. The quest + ledger remain in the DB — the web keeps
/// full history; only the channel message is ephemeral.
///
/// Best-effort + idempotent: a message already gone returns 404 (swallowed), and the row delete is an
/// <c>ExecuteDelete</c> by id, so running on more than one node (or a missed tick) is safe.
/// </summary>
public class QuestBoardCleanupScheduler(
    IServiceScopeFactory scopeFactory,
    GatewayClient client,
    ILogger<QuestBoardCleanupScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>A terminal card is due for deletion once its retention window has elapsed (0 = delete immediately).</summary>
    public static bool IsExpired(DateTimeOffset statusChangedAt, int retentionHours, DateTimeOffset now)
        => now >= statusChangedAt.AddHours(Math.Max(0, retentionHours));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Quest board cleanup started; interval {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            using var activity = BotTelemetry.StartBackgroundTick(nameof(QuestBoardCleanupScheduler));
            try
            {
                await SweepAsync(stoppingToken);
                activity.SetResult(ok: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity.SetException(ex);
                logger.LogError(ex, "Quest board cleanup failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();

        var cards = await db.ListTerminalQuestBoardCardsAsync(ct);
        if (cards.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var retentionByGuild = new Dictionary<ulong, int>();
        var removed = 0;

        foreach (var card in cards)
        {
            if (!retentionByGuild.TryGetValue(card.GuildId, out var hours))
            {
                hours = (await db.GetQuestSettingsAsync(card.GuildId, ct)).BoardRetentionHours;
                retentionByGuild[card.GuildId] = hours;
            }

            if (!IsExpired(card.StatusChangedAt, hours, now))
            {
                continue;
            }

            try
            {
                await client.Rest.DeleteMessageAsync(card.ChannelId, card.MessageId, cancellationToken: ct);
            }
            catch (RestException ex)
            {
                logger.LogWarning(ex, "Quest board message {MessageId} could not be deleted (already gone?).", card.MessageId);
            }

            await db.PostedMessages.Where(p => p.Id == card.PostedMessageId).ExecuteDeleteAsync(ct);
            removed++;
        }

        if (removed > 0)
        {
            logger.LogInformation("Quest board cleanup removed {Count} completed card(s).", removed);
        }
    }
}
