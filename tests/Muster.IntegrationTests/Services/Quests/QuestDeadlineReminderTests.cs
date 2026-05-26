using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Quests;
using Muster.IntegrationTests.TestSupport;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>The deadline-reminder query selects only un-reminded, Open quests whose deadline is inside the window,
/// and editing the deadline re-arms the nudge (clears the sent marker).</summary>
public class QuestDeadlineReminderTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>().UseInMemoryDatabase($"muster-{Guid.NewGuid()}").Options);

    private static async Task<(MusterDbContext db, Currency coin)> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();
        return (db, coin);
    }

    private static GuildQuest OpenWithDeadline(Currency coin, string name, DateTimeOffset? deadline, DateTimeOffset? remindedAt = null)
    {
        var q = GuildQuest.Create(new QuestDraft(1, 1, QuestOrigin.Guild, name, "", coin.Id, 10, Deadline: deadline));
        q.DeadlineReminderSentAt = remindedAt;
        return q;
    }

    [Fact]
    public async Task ListDueForReminder_PicksOnlyUnremindedOpenQuestsInsideTheWindow()
    {
        var (db, coin) = await SeededAsync();
        var now = DateTimeOffset.UtcNow;

        db.Quests.Add(OpenWithDeadline(coin, "Closes in 6h", now.AddHours(6)));                  // ✓ in window
        db.Quests.Add(OpenWithDeadline(coin, "Closes in 40h", now.AddHours(40)));                // ✗ beyond 24h window
        db.Quests.Add(OpenWithDeadline(coin, "Already past", now.AddHours(-2)));                 // ✗ already expired (sweep's job)
        db.Quests.Add(OpenWithDeadline(coin, "Already reminded", now.AddHours(6), now.AddHours(-1))); // ✗ sent already
        db.Quests.Add(OpenWithDeadline(coin, "No deadline", null));                              // ✗ no deadline
        var closed = OpenWithDeadline(coin, "Closed one", now.AddHours(6));
        closed.TransitionTo(QuestStatus.Closed);
        db.Quests.Add(closed);                                                                   // ✗ not Open
        await db.SaveChangesAsync();

        var due = await db.ListQuestsDueForDeadlineReminderAsync(1, now, now.AddHours(24));

        Assert.Equal("Closes in 6h", Assert.Single(due).Name);
    }

    [Fact]
    public async Task EditDeadline_RearmsTheReminder()
    {
        var (db, coin) = await SeededAsync();
        var now = DateTimeOffset.UtcNow;
        var quest = OpenWithDeadline(coin, "Patrol", now.AddHours(3), remindedAt: now.AddHours(-1));
        db.Quests.Add(quest);
        await db.SaveChangesAsync();

        var svc = new QuestService(db, new CurrencyService(db, new NullCurrencyEventSink()), new GuildAuthorizationService(db), new RecordingMessageBus());
        var result = await svc.EditAsync(quest.Id, name: null, description: null, reward: null, deadline: now.AddHours(48), tier: null, capacity: null);

        Assert.Equal(QuestResult.Ok, result);
        Assert.Null((await db.Quests.SingleAsync(q => q.Id == quest.Id)).DeadlineReminderSentAt); // re-armed for the new deadline
    }
}
