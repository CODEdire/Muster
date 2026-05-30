using Muster.Contracts;
using Muster.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Discord;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Musters;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Commands.Musters;
using Muster.Infrastructure.Commands.Quests;

namespace Muster.IntegrationTests;

public class MusterQuestCommandTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync(ulong guildId = 1, ulong ownerId = 0)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(guildId, "G", null, ownerId: ownerId);
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
        var sut = new MusterCommandService(new MusterService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db)), publisher);

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
        var sut = new MusterCommandService(new MusterService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db)), publisher);

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
        var awards = new CurrencyService(db, new RecordingMessageBus());
        var quests = new QuestService(db, awards, new GuildAuthorizationService(db), new RecordingMessageBus());

        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var quest = (await quests.PostQuestAsync(new QuestDraft(1, 5, QuestOrigin.Guild, "Recruit", "Bring a friend", points.Id, 100))).Quest!;

        Assert.Equal(QuestResult.Ok, await quests.ClaimAsync(quest.Id, 10));
        Assert.Equal(QuestResult.Ok, await quests.SubmitAsync(quest.Id, 10));
        Assert.Equal(QuestResult.Ok, await quests.ApproveAsync(quest.Id, 10, reviewerId: 5));

        Assert.Equal(100, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
    }

    [Fact]
    public async Task Quest_Claim_InvalidId_ReturnsError()
    {
        using var db = await SeededAsync();
        var board = NewBoard(db);

        Assert.True((await board.ClaimAsync(1, "nope", 10)).IsError);
        Assert.True((await board.ClaimAsync(1, Guid.NewGuid().ToString(), 10)).IsError); // unknown quest
    }

    [Fact]
    public async Task Quest_List_FormatsOpenQuests()
    {
        using var db = await SeededAsync(ownerId: 1);
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true });
        await db.SaveChangesAsync();
        var board = NewBoard(db);

        Assert.Contains("No open quests", (await board.ListAsync(1)).Message);

        await board.PostAsync(1, actorId: 1, QuestOrigin.Guild, "Recruit", "COIN", 50, description: "Bring a friend");
        var listed = await board.ListAsync(1);
        Assert.Contains("Recruit", listed.Message);
        Assert.Contains("Guild", listed.Message);
    }

    private static QuestCommandHarness NewBoard(MusterDbContext db)
    {
        var awards = new CurrencyService(db, new RecordingMessageBus());
        var auth = new GuildAuthorizationService(db);
        var quests = new QuestService(db, awards, auth, new RecordingMessageBus());
        return new QuestCommandHarness(db, auth, quests, new QuestReadService(db));
    }
}
