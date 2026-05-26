using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord.Gateway;

namespace Muster.Bot;

/// <summary>
/// DMs a "closes soon" nudge before a quest's deadline so work (or a stalled bounty) doesn't quietly expire.
/// Bot-only (needs DM REST). Once per quest, gated by <c>QuestSettings.DeadlineReminderHours</c> (0 = off) and
/// recorded on <c>GuildQuest.DeadlineReminderSentAt</c> so a missed tick / restart never double-sends. Recipients:
/// each active worker (Claimed / RevisionRequested — they still owe a submission), and, for a player bounty with no
/// taker at all, the owner (so they can extend or cancel to refund). Best-effort: a closed DM is skipped, and the
/// quest is still marked reminded so we don't retry it every tick.
/// </summary>
public class QuestReminderScheduler(
    IServiceScopeFactory scopeFactory,
    GatewayClient client,
    IConfiguration config,
    ILogger<QuestReminderScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>True when a quest should be reminded now: reminders on, not already sent, and the deadline falls in
    /// the window (still future, within <paramref name="windowHours"/>).</summary>
    public static bool IsDue(DateTimeOffset? deadline, DateTimeOffset? reminderSentAt, int windowHours, DateTimeOffset now)
        => windowHours > 0 && reminderSentAt is null && deadline is { } d && d > now && d <= now.AddHours(windowHours);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Quest deadline reminders started; interval {Interval}.", Interval);

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Quest deadline reminder sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusterDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<IQuestReadService>();
        var webBaseUrl = config["Web:BaseUrl"];
        var now = DateTimeOffset.UtcNow;
        var sent = 0;

        foreach (var guild in await db.ListActiveGuildsAsync(ct))
        {
            var hours = guild.Settings.Quests.DeadlineReminderHours;
            if (hours <= 0)
            {
                continue;
            }

            var due = await db.ListQuestsDueForDeadlineReminderAsync(guild.Id, now, now.AddHours(hours), ct);
            foreach (var quest in due)
            {
                var detail = await reads.GetQuestDetailAsync(guild.Id, quest.Id, ct);
                if (detail is not null)
                {
                    await NotifyAsync(detail, quest, guild.Id, webBaseUrl);
                    sent++;
                }

                quest.DeadlineReminderSentAt = now; // mark regardless — best-effort, never retry forever
            }

            await db.SaveChangesAsync(ct);
        }

        if (sent > 0)
        {
            logger.LogInformation("Quest deadline reminders: nudged {Count} quest(s).", sent);
        }
    }

    private async Task NotifyAsync(QuestDetailView detail, GuildQuest quest, ulong guildId, string? webBaseUrl)
    {
        var workers = quest.Participants
            .Where(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.RevisionRequested)
            .Select(p => p.UserId)
            .Distinct()
            .ToList();

        foreach (var worker in workers)
        {
            var embed = QuestEmbedRenderer.RenderDeadlineReminder(detail, guildId, ownerNoTaker: false, webBaseUrl);
            await QuestDm.TrySendAsync(client, worker, embed, []);
        }

        // A bounty about to expire with nobody on it → tell the owner (extend or cancel to refund escrow).
        var anyActive = quest.Participants.Any(p => p.Status is QuestParticipantStatus.Claimed
            or QuestParticipantStatus.Submitted or QuestParticipantStatus.RevisionRequested);
        if (!anyActive && quest.Origin == QuestOrigin.Player)
        {
            var embed = QuestEmbedRenderer.RenderDeadlineReminder(detail, guildId, ownerNoTaker: true, webBaseUrl);
            await QuestDm.TrySendAsync(client, quest.OwnerId, embed, []);
        }
    }
}
