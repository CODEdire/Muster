using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Services.Events;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Commands.Quests;
using Muster.Infrastructure.Commands.Events;

namespace Muster.IntegrationTests;

public class OpAndActivityTests
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

    [Fact]
    public async Task Op_CreateSignupClose_AwardsAttendees()
    {
        using var db = await SeededAsync();
        var sut = new OpCommandService(new GuildEventService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db)));

        await sut.CreateAsync(1, actorId: 5, name: "Raid", description: "Friday raid", reward: 75);
        var opId = (await db.GuildEvents.SingleAsync()).Id.ToString();

        await sut.SignUpAsync(1, opId, userId: 10);
        await sut.SignUpAsync(1, opId, userId: 20);
        var close = await sut.CloseAsync(1, opId);

        Assert.Contains("2", close.Message);
        Assert.Equal(75, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
        Assert.Equal(75, (await db.Wallets.SingleAsync(w => w.UserId == 20)).Balance);
    }

    [Fact]
    public async Task Op_Signup_UnknownOp_ReturnsError()
    {
        using var db = await SeededAsync();
        var sut = new OpCommandService(new GuildEventService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db)));

        Assert.True((await sut.SignUpAsync(1, "nope", 10)).IsError);
        Assert.True((await sut.SignUpAsync(1, Guid.NewGuid().ToString(), 10)).IsError);
    }

    [Fact]
    public async Task ScheduledEvent_OpensOnce_ThenCloses()
    {
        using var db = await SeededAsync();
        var sut = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));

        var first = await sut.EnsureForScheduledEventAsync(1, voiceChannelId: 500, scheduledEventId: 9001);
        var second = await sut.EnsureForScheduledEventAsync(1, voiceChannelId: 500, scheduledEventId: 9001);

        Assert.NotNull(first);
        Assert.Null(second); // idempotent — no duplicate session
        Assert.Equal(1, await db.TrackingSessions.CountAsync(s => s.Status == TrackingSessionStatus.Active));

        await sut.CloseForScheduledEventAsync(1, 9001);
        Assert.Equal(0, await db.TrackingSessions.CountAsync(s => s.Status == TrackingSessionStatus.Active));
    }

    [Fact]
    public async Task MessageActivity_RecordsRollup_AndDedupes()
    {
        using var db = await SeededAsync();
        db.TrackedChannels.Add(new TrackedChannel
        {
            Id = Guid.NewGuid(), GuildId = 1, ChannelId = 100,
            Kind = TrackedChannelKind.Text, Mode = TrackedChannelMode.StatsOnly,
        });
        await db.SaveChangesAsync();

        var sut = new ActivityService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));
        var now = DateTimeOffset.UtcNow;

        await sut.RecordMessageAsync(1, channelId: 100, userId: 10, messageId: 555, now);
        await sut.RecordMessageAsync(1, channelId: 100, userId: 10, messageId: 555, now); // duplicate id
        await sut.RecordMessageAsync(1, channelId: 100, userId: 10, messageId: 556, now);

        Assert.Equal(2, await db.ActivityRecords.CountAsync()); // 555 once + 556
        var rollup = await db.DailyActivityRollups.SingleAsync();
        Assert.Equal(2, rollup.MessageCount);
    }

    [Fact]
    public async Task MessageActivity_UntrackedChannel_Ignored()
    {
        using var db = await SeededAsync();
        var sut = new ActivityService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));

        await sut.RecordMessageAsync(1, channelId: 999, userId: 10, messageId: 1, DateTimeOffset.UtcNow);

        Assert.Equal(0, await db.ActivityRecords.CountAsync()); // not a tracked text channel
    }

    [Fact]
    public async Task MessageActivity_RewardChannel_AwardsPointsPerMessage()
    {
        using var db = await SeededAsync();
        db.TrackedChannels.Add(new TrackedChannel
        {
            Id = Guid.NewGuid(), GuildId = 1, ChannelId = 100,
            Kind = TrackedChannelKind.Text, Mode = TrackedChannelMode.Reward, PointsPerMessage = 3,
        });
        await db.SaveChangesAsync();

        var sut = new ActivityService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));

        await sut.RecordMessageAsync(1, channelId: 100, userId: 10, messageId: 1, DateTimeOffset.UtcNow);

        Assert.Equal(3, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
        Assert.Equal(1, await db.ActivityRecords.CountAsync());
    }
}
