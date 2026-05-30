using Muster.Bot.Economy;
using Muster.Bot.Questing;
using Muster.Domain.Enums;
using Xunit;

namespace Muster.Bot.UnitTests.Questing.BackgroundServices;

/// <summary>Pure routing: which channel a quest's card belongs in for each state.</summary>
public class QuestBoardRoutingTests
{
    private const ulong Public = 100, Mod = 200;

    [Theory]
    [InlineData(QuestStatus.Open, Public)]
    [InlineData(QuestStatus.Closed, Public)]
    [InlineData(QuestStatus.Cancelled, Public)]
    [InlineData(QuestStatus.PendingApproval, Mod)]
    [InlineData(QuestStatus.Disputed, Mod)]
    [InlineData(QuestStatus.PendingFinal, Mod)]
    public void RoutesByPhase(QuestStatus status, ulong expected)
        => Assert.Equal(expected, QuestBoardNotificationHandler.TargetChannel(status, Public, Mod));

    [Fact]
    public void Scheduled_PostsNowhere()
        => Assert.Equal(0UL, QuestBoardNotificationHandler.TargetChannel(QuestStatus.Scheduled, Public, Mod));

    [Fact]
    public void ModState_NoModChannel_PostsNowhere()
        => Assert.Equal(0UL, QuestBoardNotificationHandler.TargetChannel(QuestStatus.PendingApproval, Public, modChannel: 0));
}

/// <summary>The retention decision is pure, so we assert when a completed card is due for deletion without Discord.</summary>
public class QuestBoardCleanupTests
{
    private static readonly DateTimeOffset Terminal = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NotExpired_WithinRetentionWindow()
        => Assert.False(QuestBoardCleanupScheduler.IsExpired(Terminal, retentionHours: 48, now: Terminal.AddHours(47)));

    [Fact]
    public void Expired_OnceWindowElapsed()
        => Assert.True(QuestBoardCleanupScheduler.IsExpired(Terminal, retentionHours: 48, now: Terminal.AddHours(48)));

    [Fact]
    public void ZeroRetention_ExpiresImmediately()
        => Assert.True(QuestBoardCleanupScheduler.IsExpired(Terminal, retentionHours: 0, now: Terminal));

    [Fact]
    public void NegativeRetention_TreatedAsZero()
        => Assert.True(QuestBoardCleanupScheduler.IsExpired(Terminal, retentionHours: -10, now: Terminal));
}

/// <summary>The deadline-reminder "is due now" decision is pure — assert the window logic without Discord/DB.</summary>
public class QuestReminderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Due_WhenDeadlineInsideWindow_AndNotYetSent()
        => Assert.True(QuestReminderScheduler.IsDue(Now.AddHours(6), reminderSentAt: null, windowHours: 24, now: Now));

    [Fact]
    public void NotDue_WhenDeadlineBeyondWindow()
        => Assert.False(QuestReminderScheduler.IsDue(Now.AddHours(40), reminderSentAt: null, windowHours: 24, now: Now));

    [Fact]
    public void NotDue_WhenDeadlineAlreadyPast()
        => Assert.False(QuestReminderScheduler.IsDue(Now.AddHours(-1), reminderSentAt: null, windowHours: 24, now: Now));

    [Fact]
    public void NotDue_WhenAlreadyReminded()
        => Assert.False(QuestReminderScheduler.IsDue(Now.AddHours(6), reminderSentAt: Now.AddHours(-1), windowHours: 24, now: Now));

    [Fact]
    public void NotDue_WhenRemindersDisabled()
        => Assert.False(QuestReminderScheduler.IsDue(Now.AddHours(6), reminderSentAt: null, windowHours: 0, now: Now));

    [Fact]
    public void NotDue_WhenNoDeadline()
        => Assert.False(QuestReminderScheduler.IsDue(deadline: null, reminderSentAt: null, windowHours: 24, now: Now));
}
