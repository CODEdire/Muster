using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

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
        var missions = new MissionService(db, new AwardService(db), new GuildAuthorizationService(db), new NullQuestNotifier(), new NullQuestRewardSink());
        var sut = new QuestCommandService(missions, db);

        // Owner has zero COIN, but a guild quest mints — should succeed.
        var result = await sut.PostGuildQuestAsync(1, actorId: 1, "Patrol", "Run a patrol", "COIN", 100);

        Assert.False(result.IsError);
        var quest = await db.Missions.SingleAsync();
        Assert.Equal(MissionOrigin.Guild, quest.Origin);
        Assert.Equal(0, quest.EscrowAmount); // guild quests don't escrow
    }

    [Fact]
    public async Task GuildQuest_UnknownCurrency_Errors()
    {
        using var db = await SeededAsync();
        var sut = new QuestCommandService(new MissionService(db, new AwardService(db), new GuildAuthorizationService(db), new NullQuestNotifier(), new NullQuestRewardSink()), db);

        Assert.True((await sut.PostGuildQuestAsync(1, 1, "Patrol", "", "NOPE", 100)).IsError);
    }

    [Fact]
    public async Task Award_ByCurrency_CreditsChosenCurrency()
    {
        using var db = await SeededAsync();
        var sut = new AwardCommandService(new ManualAwardService(db, new AwardService(db)));

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
        var sut = new AwardCommandService(new ManualAwardService(db, new AwardService(db)));

        Assert.True((await sut.AwardCurrencyAsync(1, 1, 10, "NOPE", 25, "loot")).IsError);
    }
}
