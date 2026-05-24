using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Discord;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

public class MusterQuestCommandTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync(ulong guildId = 1)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(guildId, "G", null);
        return db;
    }

    /// <summary>A fake publisher records the post instead of calling Discord, returning a fixed message id.</summary>
    private sealed class FakePublisher : IMusterPublisher
    {
        public ulong PublishedChannel { get; private set; }
        public string? PublishedEmoji { get; private set; }
        public int Calls { get; private set; }

        public Task<ulong> PublishAsync(ulong channelId, string prompt, string emoji, CancellationToken ct = default)
        {
            Calls++;
            PublishedChannel = channelId;
            PublishedEmoji = emoji;
            return Task.FromResult(777ul);
        }
    }

    [Fact]
    public async Task Muster_Create_PostsAndStoresWithReturnedMessageId()
    {
        using var db = await SeededAsync();
        var publisher = new FakePublisher();
        var sut = new MusterCommandService(new MusterService(db, new AwardService(db), new GuildAuthorizationService(db)), publisher);

        var result = await sut.CreateAsync(1, channelId: 500, prompt: "Roll call", emoji: "✅", reward: 10, capacity: null);

        Assert.False(result.IsError);
        Assert.Equal(1, publisher.Calls);
        Assert.Equal(500ul, publisher.PublishedChannel);

        var muster = await db.ReactionMusters.SingleAsync();
        Assert.Equal(777ul, muster.MessageId);
        Assert.Equal(10, muster.RewardAmount);
        Assert.Contains("✅", muster.Emojis);
    }

    [Fact]
    public async Task Muster_Create_RejectsBadInput_WithoutPublishing()
    {
        using var db = await SeededAsync();
        var publisher = new FakePublisher();
        var sut = new MusterCommandService(new MusterService(db, new AwardService(db), new GuildAuthorizationService(db)), publisher);

        Assert.True((await sut.CreateAsync(1, 500, "", "✅", 10, null)).IsError);
        Assert.True((await sut.CreateAsync(1, 500, "p", "✅", -1, null)).IsError);
        Assert.True((await sut.CreateAsync(1, 500, "p", "✅", 10, 0)).IsError);

        Assert.Equal(0, publisher.Calls);
        Assert.Equal(0, await db.ReactionMusters.CountAsync());
    }

    [Fact]
    public async Task Quest_PostClaimSubmitApprove_AwardsMember()
    {
        using var db = await SeededAsync();
        var missions = new MissionService(db, new AwardService(db), new GuildAuthorizationService(db));
        var sut = new QuestCommandService(missions, db);

        var post = await sut.PostAsync(1, actorId: 5, name: "Recruit", description: "Bring a friend", reward: 100);
        Assert.False(post.IsError);
        var quest = await db.Missions.SingleAsync();
        var id = quest.Id.ToString();

        Assert.False((await sut.ClaimAsync(1, id, userId: 10)).IsError);
        Assert.False((await sut.SubmitAsync(1, id, userId: 10)).IsError);
        Assert.False((await sut.ApproveAsync(1, id, memberId: 10, reviewerId: 5)).IsError);

        Assert.Equal(100, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
    }

    [Fact]
    public async Task Quest_Claim_InvalidId_ReturnsError()
    {
        using var db = await SeededAsync();
        var sut = new QuestCommandService(new MissionService(db, new AwardService(db), new GuildAuthorizationService(db)), db);

        Assert.True((await sut.ClaimAsync(1, "nope", 10)).IsError);
        Assert.True((await sut.ClaimAsync(1, Guid.NewGuid().ToString(), 10)).IsError); // unknown quest
    }

    [Fact]
    public async Task Quest_List_FormatsOpenQuests()
    {
        using var db = await SeededAsync();
        var missions = new MissionService(db, new AwardService(db), new GuildAuthorizationService(db));
        var sut = new QuestCommandService(missions, db);

        Assert.Equal("No open quests right now.", (await sut.ListAsync(1)).Message);

        await sut.PostAsync(1, 5, "Recruit", "Bring a friend", 50);
        var listed = await sut.ListAsync(1);
        Assert.Contains("Recruit", listed.Message);
        Assert.Contains("reward 50", listed.Message);
    }
}
