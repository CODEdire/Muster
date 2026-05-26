using Muster.Contracts;
using Muster.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Quests;

namespace Muster.IntegrationTests;

public class QuestSweepTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private sealed record Ctx(MusterDbContext Db, QuestService Quests, Currency Coin);

    private static async Task<Ctx> SeededAsync(ulong guildId)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(guildId, $"G{guildId}", null, ownerId: guildId);
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = guildId, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var awards = new CurrencyService(db, new RecordingMessageBus());
        var auth = new GuildAuthorizationService(db);
        return new Ctx(db, new QuestService(db, awards, auth, new RecordingMessageBus()), coin);
    }

    private static Task<long> BalanceAsync(MusterDbContext db, ulong userId, Guid currencyId) =>
        db.CurrencyLedgerEntries.Where(e => e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == null)
            .SumAsync(e => (long?)e.Amount).ContinueWith(t => t.Result ?? 0);

    [Fact]
    public async Task GlobalSweep_ActivatesAndExpires_AcrossGuilds()
    {
        var g1 = await SeededAsync(1);
        var g2 = await SeededAsync(2);

        // g1: a scheduled quest whose start has passed → should activate.
        await g1.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Patrol", "", g1.Coin.Id, 50,
            StartsAt: DateTimeOffset.UtcNow.AddMinutes(5)));
        var scheduled = await g1.Db.Quests.SingleAsync();
        scheduled.ScheduledStart = DateTimeOffset.UtcNow.AddMinutes(-1); // now due
        await g1.Db.SaveChangesAsync();

        // g2: a personal quest past its deadline → should expire and refund.
        await new CurrencyService(g2.Db, new RecordingMessageBus()).AwardAsync(2, 20, g2.Coin.Id, 100, CurrencyLedgerSource.Connector, null, "seed");
        await g2.Quests.PostQuestAsync(new QuestDraft(2, 20, QuestOrigin.Player, "Escort", "", g2.Coin.Id, 40, Deadline: DateTimeOffset.UtcNow.AddHours(-1)));

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await g1.Quests.ActivateScheduledAsync(now));
        Assert.Equal(QuestStatus.Open, (await g1.Db.Quests.SingleAsync()).Status);

        Assert.Equal(1, await g2.Quests.ExpireDueAsync(now));
        Assert.Equal(QuestStatus.Expired, (await g2.Db.Quests.SingleAsync()).Status);
        Assert.Equal(100, await BalanceAsync(g2.Db, 20, g2.Coin.Id)); // escrow refunded
    }

    [Fact]
    public async Task Sweep_IsIdempotent_AcrossRepeatedRuns()
    {
        var c = await SeededAsync(1);
        await new CurrencyService(c.Db, new RecordingMessageBus()).AwardAsync(1, 20, c.Coin.Id, 100, CurrencyLedgerSource.Connector, null, "seed");
        await c.Quests.PostQuestAsync(new QuestDraft(1, 20, QuestOrigin.Player, "Escort", "", c.Coin.Id, 40, Deadline: DateTimeOffset.UtcNow.AddHours(-1)));

        var now = DateTimeOffset.UtcNow;
        await c.Quests.ExpireDueAsync(now);
        await c.Quests.ExpireDueAsync(now);

        // A second sweep must not double-refund.
        Assert.Equal(100, await BalanceAsync(c.Db, 20, c.Coin.Id));
        Assert.Equal(QuestStatus.Expired, (await c.Db.Quests.SingleAsync()).Status);
    }

    [Fact]
    public async Task GuildQuest_PastDeadline_Expires_WithoutRefund()
    {
        var c = await SeededAsync(1);
        await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Patrol", "", c.Coin.Id, 50,
            Deadline: DateTimeOffset.UtcNow.AddHours(-1)));

        // The player-quest sweep must ignore it (it's guild-funded)...
        Assert.Equal(0, await c.Quests.ExpireDueAsync(DateTimeOffset.UtcNow));
        // ...and the guild-quest sweep must close it.
        Assert.Equal(1, await c.Quests.ExpireDueQuestsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(QuestStatus.Expired, (await c.Db.Quests.SingleAsync()).Status);
    }

    [Fact]
    public async Task GuildQuest_AwaitingApproval_IsNotExpired()
    {
        var c = await SeededAsync(1);
        await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Patrol", "", c.Coin.Id, 50,
            Deadline: DateTimeOffset.UtcNow.AddHours(-1)));
        var quest = await c.Db.Quests.SingleAsync();
        await c.Quests.ClaimAsync(quest.Id, 20);
        await c.Quests.SubmitAsync(quest.Id, 20);

        Assert.Equal(0, await c.Quests.ExpireDueQuestsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(QuestStatus.Open, (await c.Db.Quests.SingleAsync()).Status);
    }

    [Fact]
    public async Task GlobalActivate_LeavesFutureScheduledQuestsAlone()
    {
        var c = await SeededAsync(1);
        await c.Quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Future", "", c.Coin.Id, 50,
            StartsAt: DateTimeOffset.UtcNow.AddHours(2)));

        Assert.Equal(0, await c.Quests.ActivateScheduledAsync(DateTimeOffset.UtcNow));
        Assert.Equal(QuestStatus.Scheduled, (await c.Db.Quests.SingleAsync()).Status);
    }
}
