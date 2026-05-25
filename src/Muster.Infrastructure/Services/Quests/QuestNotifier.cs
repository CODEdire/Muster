using Microsoft.Extensions.Logging;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Ledger;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>Lifecycle moments worth telling someone about. Wired to Discord later.</summary>
public enum QuestLifecycleEvent
{
    /// <summary>A quest was created/opened — for a formatted board post (later).</summary>
    Created,
    PendingApproval,
    Accepted,
    RejectedAtIntake,
    Claimed,
    Submitted,
    RevisionRequested,
    AwaitingFinalApproval,
    Settled,
    Refunded,
    Disputed,
    Expired,
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

/// <summary>Notifier that drops everything — a safe default for tests and contexts with no delivery wired.</summary>
public sealed class NullQuestNotifier : IQuestNotifier
{
    public Task NotifyAsync(QuestNotification notification, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// A completed quest, emitted so an external rewards connector (the "CurrencyService" / loot system) can
/// resolve or mint rewards beyond Muster's own ledger. Fired once per completer when they're paid/minted.
/// </summary>
public record QuestCompletion(
    ulong GuildId,
    Guid MissionId,
    string MissionName,
    ulong CompleterUserId,
    MissionOrigin Origin,
    Guid RewardCurrencyId,
    long RewardAmount,
    long BonusPoints,
    QuestTier Tier);

/// <summary>Seam for quest-completion reward resolution. The default logs; a connector implementation handles it later.</summary>
public interface IQuestRewardSink
{
    Task OnCompletedAsync(QuestCompletion completion, CancellationToken ct = default);
}

/// <summary>No-op reward sink — safe default for tests and until an external connector is wired up.</summary>
public sealed class NullQuestRewardSink : IQuestRewardSink
{
    public Task OnCompletedAsync(QuestCompletion completion, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Logs completion events until the external rewards connector is wired up.</summary>
public class LoggingQuestRewardSink(ILogger<LoggingQuestRewardSink> logger) : IQuestRewardSink
{
    public Task OnCompletedAsync(QuestCompletion completion, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Quest completed: guild {GuildId} quest {MissionId} ({MissionName}) completer {User} reward {Amount} {Currency} +{Bonus} pts tier {Tier}",
            completion.GuildId, completion.MissionId, completion.MissionName, completion.CompleterUserId,
            completion.RewardAmount, completion.RewardCurrencyId, completion.BonusPoints, completion.Tier);
        return Task.CompletedTask;
    }
}
