using Microsoft.Extensions.Logging;

namespace Muster.Infrastructure.Services;

/// <summary>Lifecycle moments worth telling someone about. Wired to Discord later.</summary>
public enum QuestLifecycleEvent
{
    PendingApproval,
    Accepted,
    RejectedAtIntake,
    Submitted,
    RevisionRequested,
    AwaitingFinalApproval,
    Settled,
    Refunded,
    Disputed,
    AutoResolved,
}

/// <summary>A lifecycle notification. <paramref name="TargetUserId"/> is who should hear about it (null = managers).</summary>
public record QuestNotification(
    ulong GuildId,
    Guid MissionId,
    string MissionName,
    QuestLifecycleEvent Event,
    ulong? TargetUserId,
    string Detail);

/// <summary>
/// Seam for quest lifecycle notifications. The default implementation just logs; a Discord-backed
/// implementation (DMs / channel posts) is registered by the bot host later.
/// </summary>
public interface IQuestNotifier
{
    Task NotifyAsync(QuestNotification notification, CancellationToken ct = default);
}

/// <summary>No-op notifier that records intent to the log until Discord delivery is wired up.</summary>
public class LoggingQuestNotifier(ILogger<LoggingQuestNotifier> logger) : IQuestNotifier
{
    public Task NotifyAsync(QuestNotification notification, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Quest notification [{Event}] guild {GuildId} quest {MissionId} ({MissionName}) target {Target}: {Detail}",
            notification.Event, notification.GuildId, notification.MissionId, notification.MissionName,
            notification.TargetUserId?.ToString() ?? "managers", notification.Detail);
        return Task.CompletedTask;
    }
}
