using Muster.Contracts;
using Muster.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Xunit;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Commands.Ledger;
using Muster.Infrastructure.Commands.Quests;

namespace Muster.IntegrationTests;

public class CurrencyAwareCommandTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task GuildQuest_Mints_NoBalanceRequired()
    {
        using var db = await SeededAsync();
        var board = NewBoard(db);

        // Owner has zero COIN, but a guild quest mints — should succeed.
        var result = await board.PostAsync(1, actorId: 1, QuestOrigin.Guild, "Patrol", "COIN", 100, description: "Run a patrol");

        Assert.False(result.IsError);
        var quest = await db.Quests.SingleAsync();
        Assert.Equal(QuestOrigin.Guild, quest.Origin);
        Assert.Equal(0, quest.EscrowAmount); // guild quests don't escrow
    }

    [Fact]
    public async Task GuildQuest_UnknownCurrency_Errors()
    {
        using var db = await SeededAsync();
        var board = NewBoard(db);

        Assert.True((await board.PostAsync(1, actorId: 1, QuestOrigin.Guild, "Patrol", "NOPE", 100)).IsError);
    }

    private static QuestCommandHarness NewBoard(MusterDbContext db)
    {
        var awards = new CurrencyService(db, new NullCurrencyEventSink());
        var auth = new GuildAuthorizationService(db);
        var quests = new QuestService(db, awards, auth, new RecordingMessageBus());
        return new QuestCommandHarness(db, auth, quests, new QuestReadService(db));
    }

    [Fact]
    public async Task Award_ByCurrency_CreditsChosenCurrency()
    {
        using var db = await SeededAsync();
        var sut = new AwardCommandService(new ManualAwardService(db, new CurrencyService(db, new NullCurrencyEventSink())));

        var result = await sut.AwardCurrencyAsync(1, actorId: 1, memberId: 10, "COIN", 25, "loot");

        Assert.False(result.IsError);
        var coin = await db.Currencies.SingleAsync(c => c.Code == "COIN");
        var balance = await db.LedgerEntries
            .Where(e => e.UserId == 10 && e.CurrencyId == coin.Id)
            .SumAsync(e => (long?)e.Amount) ?? 0;
        Assert.Equal(25, balance);
    }

    [Fact]
    public async Task Award_UnknownCurrency_Errors()
    {
        using var db = await SeededAsync();
        var sut = new AwardCommandService(new ManualAwardService(db, new CurrencyService(db, new NullCurrencyEventSink())));

        Assert.True((await sut.AwardCurrencyAsync(1, 1, 10, "NOPE", 25, "loot")).IsError);
    }
}
