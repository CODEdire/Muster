using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Quests;
using Xunit;

namespace Muster.IntegrationTests;

public class QuestReadServiceTests
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

    [Fact]
    public async Task GetBoardAsync_ActiveTab_ProjectsOpenQuests_WithCodeMap()
    {
        var (db, coin) = await SeededAsync();
        db.Quests.Add(GuildQuest.Create(new QuestDraft(1, 1, QuestOrigin.Guild, "Open one", "do it", coin.Id, 50)));
        var closed = GuildQuest.Create(new QuestDraft(1, 1, QuestOrigin.Guild, "Done", "", coin.Id, 10));
        closed.TransitionTo(QuestStatus.Closed);
        db.Quests.Add(closed);
        await db.SaveChangesAsync();

        var page = await new QuestReadService(db).GetBoardAsync(1, 99, isManager: false, "active", null, null, "created", true, 1, 25);

        var item = Assert.Single(page.Items); // closed one excluded from the active tab
        Assert.Equal("Open one", item.Name);
        Assert.Equal("COIN", page.Codes[coin.Id]);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task GetBoardAsync_ActionNeededTab_IsManagerReviewQueueOnly()
    {
        var (db, coin) = await SeededAsync();
        var disputed = GuildQuest.Create(new QuestDraft(1, 1, QuestOrigin.Player, "Bounty", "", coin.Id, 30));
        disputed.TransitionTo(QuestStatus.Disputed);
        db.Quests.Add(disputed);
        db.Quests.Add(GuildQuest.Create(new QuestDraft(1, 1, QuestOrigin.Guild, "Just open", "", coin.Id, 10))); // not awaiting action
        await db.SaveChangesAsync();

        var asManager = await new QuestReadService(db).GetBoardAsync(1, 99, isManager: true, "actionneeded", null, null, "created", true, 1, 25);
        Assert.Equal("Bounty", Assert.Single(asManager.Items).Name);

        var asMember = await new QuestReadService(db).GetBoardAsync(1, 99, isManager: false, "actionneeded", null, null, "created", true, 1, 25);
        Assert.Empty(asMember.Items); // non-managers have no review queue
    }

    [Fact]
    public async Task GetBoardAsync_MineScope_IsBountiesIPostedPlusAnythingIClaimed()
    {
        var (db, coin) = await SeededAsync();
        db.Quests.Add(GuildQuest.Create(new QuestDraft(1, 7, QuestOrigin.Player, "My bounty", "", coin.Id, 20)));
        db.Quests.Add(GuildQuest.Create(new QuestDraft(1, 99, QuestOrigin.Player, "Their bounty", "", coin.Id, 20)));
        // Guild duty I posted but did NOT claim → excluded (official duty, not personal).
        db.Quests.Add(GuildQuest.Create(new QuestDraft(1, 7, QuestOrigin.Guild, "Unclaimed duty", "", coin.Id, 20)));
        // Guild duty I claimed → included (relates to me personally).
        var claimed = GuildQuest.Create(new QuestDraft(1, 1, QuestOrigin.Guild, "Claimed duty", "", coin.Id, 20));
        claimed.Participants.Add(QuestParticipant.NewClaim(claimed.Id, 7));
        db.Quests.Add(claimed);
        await db.SaveChangesAsync();

        var page = await new QuestReadService(db).GetBoardAsync(1, 7, isManager: false, "active", "mine", null, "created", true, 1, 25);

        var names = page.Items.Select(i => i.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Claimed duty", "My bounty" }, names);
    }

    [Fact]
    public async Task GetForEditAsync_ReturnsTheEditableFields_OrNull()
    {
        var (db, coin) = await SeededAsync();
        var quest = GuildQuest.Create(new QuestDraft(1, 1, QuestOrigin.Guild, "Patrol", "watch", coin.Id, 75, Tier: QuestTier.B, Capacity: 3));
        db.Quests.Add(quest);
        await db.SaveChangesAsync();

        var view = await new QuestReadService(db).GetForEditAsync(1, quest.Id);

        Assert.NotNull(view);
        Assert.Equal("Patrol", view!.Name);
        Assert.Equal(75, view.RewardAmount);
        Assert.Equal(QuestTier.B, view.Tier);
        Assert.Equal(3, view.Capacity);
        Assert.Null(await new QuestReadService(db).GetForEditAsync(1, Guid.NewGuid())); // unknown id
    }

    [Fact]
    public async Task GetOpenBoardAsync_ReturnsLiveQuestsWithState()
    {
        var (db, coin) = await SeededAsync();
        db.Quests.Add(GuildQuest.Create(new QuestDraft(1, 1, QuestOrigin.Guild, "Recruit", "", coin.Id, 40)));
        await db.SaveChangesAsync();

        var items = await new QuestReadService(db).GetOpenBoardAsync(1);

        var item = Assert.Single(items);
        Assert.Equal("Recruit", item.Name);
        Assert.Equal("COIN", item.Code);
        Assert.Equal("open", item.State);
    }
}
