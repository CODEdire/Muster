using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Persistence.Queries;
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

    /// <summary>A voice roster snapshot: the given users present (unmuted, human) in one channel.</summary>
    private static IReadOnlyDictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> Occupant(ulong channelId, params ulong[] userIds)
        => new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>>
        {
            [channelId] = userIds.Select(u => new VoiceMemberSnapshot(u, IsBot: false, IsMuted: false, IsDeafened: false)).ToList(),
        };

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
        var sut = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db));

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
        db.GuildChannels.Add(new GuildChannel
        {
            GuildId = 1, ChannelId = 100,
            Kind = GuildChannelKind.Text, Mode = TrackedChannelMode.StatsOnly,
        });
        await db.SaveChangesAsync();

        var sut = new ActivityService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db));
        var now = DateTimeOffset.UtcNow;

        await sut.RecordMessageAsync(1, channelId: 100, userId: 10, messageId: 555, now);
        await sut.RecordMessageAsync(1, channelId: 100, userId: 10, messageId: 555, now); // duplicate id
        await sut.RecordMessageAsync(1, channelId: 100, userId: 10, messageId: 556, now);

        Assert.Equal(2, await db.ActivityRecords.CountAsync()); // 555 once + 556
        var rollup = await db.DailyActivityRollups.SingleAsync();
        Assert.Equal(2, rollup.MessageCount);
    }

    [Fact]
    public async Task SessionClose_MintsSessionCoin_ByMinutes()
    {
        using var db = await SeededAsync();
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        var guild = await db.FindGuildAsync(1);
        guild!.Settings.SessionCoinCurrencyCode = "COIN";
        guild.Settings.MinutesPerCoin = 30;
        guild.Settings.ApplyAfkGuardsToSessions = false; // single-user accrual test
        guild.Settings = guild.Settings;
        await db.SaveChangesAsync();

        var sut = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db));
        var now = DateTimeOffset.UtcNow;
        var session = await sut.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);
        await sut.ReconcileSessionsAsync(1, Occupant(500, 10), now);
        await sut.CloseAsync(session.Id, at: now.AddMinutes(60)); // 60 eligible minutes

        var coinBalance = (await db.Wallets.SingleAsync(w => w.UserId == 10 && w.CurrencyId == coin.Id)).Balance;
        Assert.Equal(2, coinBalance); // floor(60 / 30)
    }

    [Fact]
    public async Task SessionClose_AppliesRewardMultiplier_ToCoin()
    {
        using var db = await SeededAsync();
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        var guild = await db.FindGuildAsync(1);
        guild!.Settings.SessionCoinCurrencyCode = "COIN";
        guild.Settings.MinutesPerCoin = 30;
        guild.Settings.ApplyAfkGuardsToSessions = false;
        guild.Settings = guild.Settings;
        var now = DateTimeOffset.UtcNow;
        db.RewardMultipliers.Add(new RewardMultiplier
        {
            Id = Guid.NewGuid(), GuildId = 1, Name = "2x", Factor = 2m, Kind = MultiplierKind.OneOff,
            Scope = MultiplierScope.All, Enabled = true, StartsAt = now.AddHours(-1), EndsAt = now.AddHours(2),
        });
        await db.SaveChangesAsync();

        var sut = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db));
        var session = await sut.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);
        await sut.ReconcileSessionsAsync(1, Occupant(500, 10), now);
        await sut.CloseAsync(session.Id, at: now.AddMinutes(60)); // 60 eligible minutes, weighted ×2 = 120

        var coinBalance = (await db.Wallets.SingleAsync(w => w.UserId == 10 && w.CurrencyId == coin.Id)).Balance;
        Assert.Equal(4, coinBalance); // floor(60 × 2 / 30)
    }

    [Fact]
    public async Task SessionClose_AwardsStartAndEndPresenceBonuses()
    {
        using var db = await SeededAsync();
        var guild = await db.FindGuildAsync(1);
        guild!.Settings.ApplyAfkGuardsToSessions = false;
        guild.Settings.SessionStartBonus = 50;
        guild.Settings.SessionEndBonus = 25;
        guild.Settings = guild.Settings;
        await db.SaveChangesAsync();

        var sut = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db));
        var now = DateTimeOffset.UtcNow;
        var session = await sut.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);
        await sut.ReconcileSessionsAsync(1, Occupant(500, 10), now);      // present at start
        await sut.CloseAsync(session.Id, at: now.AddMinutes(30));         // still present at end

        var points = await db.FindPointsAsync(1);
        var seasonId = points!.IsSeasonal ? await db.ActiveSeasonIdAsync(1) : (Guid?)null;
        var balance = await db.BalanceAsync(1, 10, points.Id, seasonId);
        Assert.Equal(105, balance); // 30 min × 1 + 50 start + 25 end
    }

    [Fact]
    public async Task SetSessionCoin_RejectsNonSpendableAndUnknown_AcceptsSpendable()
    {
        using var db = await SeededAsync();
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "SP", Name = "Spend", IsSpendable = true });
        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "NS", Name = "NotSpend", IsSpendable = false });
        await db.SaveChangesAsync();
        var sut = new Muster.Infrastructure.Commands.Membership.ConfigCommandService(
            db, Microsoft.Extensions.Options.Options.Create(new Muster.Infrastructure.Services.Currencies.CurrencyRetentionOptions()));

        Assert.True((await sut.SetSessionCoinAsync(1, "MISSING", 30)).IsError);
        Assert.True((await sut.SetSessionCoinAsync(1, "NS", 30)).IsError);
        Assert.False((await sut.SetSessionCoinAsync(1, "SP", 30)).IsError);

        var settings = (await db.FindGuildAsync(1))!.Settings;
        Assert.Equal("SP", settings.SessionCoinCurrencyCode);
        Assert.Equal(30, settings.MinutesPerCoin);
    }

    private static ActivityService Activity(MusterDbContext db)
        => new(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db));

    private static async Task AddRewardTextAsync(MusterDbContext db, int pointsPerMessage, int messagesPerPoint = 1, int cooldown = 0, int dailyCap = 0)
    {
        db.GuildChannels.Add(new GuildChannel
        {
            GuildId = 1, ChannelId = 100, Kind = GuildChannelKind.Text, Mode = TrackedChannelMode.Reward,
            PointsPerMessage = pointsPerMessage, MessagesPerPoint = messagesPerPoint, MessageCooldownSeconds = cooldown, MessageDailyCapPoints = dailyCap,
        });
        await db.SaveChangesAsync();
    }

    private static Task<long> BalanceAsync(MusterDbContext db, ulong userId)
        => db.Wallets.Where(w => w.UserId == userId).Select(w => w.Balance).SingleOrDefaultAsync();

    [Fact]
    public async Task MessageReward_MessagesPerPoint_AwardsEveryNth()
    {
        using var db = await SeededAsync();
        await AddRewardTextAsync(db, pointsPerMessage: 2, messagesPerPoint: 3);
        var sut = Activity(db);
        var t = DateTimeOffset.UtcNow;

        await sut.RecordMessageAsync(1, 100, 10, 1, t);
        await sut.RecordMessageAsync(1, 100, 10, 2, t);
        Assert.Equal(0, await BalanceAsync(db, 10)); // 2 of 3
        await sut.RecordMessageAsync(1, 100, 10, 3, t);
        Assert.Equal(2, await BalanceAsync(db, 10)); // 3rd message → 2 points
    }

    [Fact]
    public async Task MessageReward_DailyCap_Clamps()
    {
        using var db = await SeededAsync();
        await AddRewardTextAsync(db, pointsPerMessage: 5, dailyCap: 8);
        var sut = Activity(db);
        var t = DateTimeOffset.UtcNow;

        await sut.RecordMessageAsync(1, 100, 10, 1, t); // +5 = 5
        await sut.RecordMessageAsync(1, 100, 10, 2, t); // +3 (clamped) = 8
        await sut.RecordMessageAsync(1, 100, 10, 3, t); // capped, +0
        Assert.Equal(8, await BalanceAsync(db, 10));
    }

    [Fact]
    public async Task MessageReward_Cooldown_BlocksWithinWindow()
    {
        using var db = await SeededAsync();
        await AddRewardTextAsync(db, pointsPerMessage: 1, cooldown: 60);
        var sut = Activity(db);
        var t = DateTimeOffset.UtcNow;

        await sut.RecordMessageAsync(1, 100, 10, 1, t);                    // +1
        await sut.RecordMessageAsync(1, 100, 10, 2, t.AddSeconds(30));     // within cooldown → +0
        await sut.RecordMessageAsync(1, 100, 10, 3, t.AddSeconds(61));     // past cooldown → +1
        Assert.Equal(2, await BalanceAsync(db, 10));
    }

    [Fact]
    public async Task PruneOldRecords_DeletesBeyondRetention_KeepsRecent()
    {
        using var db = await SeededAsync();
        var guild = await db.FindGuildAsync(1);
        guild!.Settings.ActivityRetentionDays = 30;
        guild.Settings = guild.Settings;
        var now = DateTimeOffset.UtcNow;
        db.ActivityRecords.Add(new ActivityRecord { GuildId = 1, ChannelId = 100, UserId = 10, Type = ActivityType.Message, Timestamp = now.AddDays(-40), SourceMessageId = 1 });
        db.ActivityRecords.Add(new ActivityRecord { GuildId = 1, ChannelId = 100, UserId = 10, Type = ActivityType.Message, Timestamp = now.AddDays(-1), SourceMessageId = 2 });
        await db.SaveChangesAsync();

        var pruned = await Activity(db).PruneOldRecordsAsync(now);

        Assert.Equal(1, pruned);
        var remaining = await db.ActivityRecords.SingleAsync();
        Assert.Equal(2ul, remaining.SourceMessageId); // the recent one survives
    }

    [Fact]
    public async Task MessageActivity_UntrackedChannel_Ignored()
    {
        using var db = await SeededAsync();
        var sut = new ActivityService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db));

        await sut.RecordMessageAsync(1, channelId: 999, userId: 10, messageId: 1, DateTimeOffset.UtcNow);

        Assert.Equal(0, await db.ActivityRecords.CountAsync()); // not a tracked text channel
    }

    [Fact]
    public async Task MessageActivity_RewardChannel_AwardsPointsPerMessage()
    {
        using var db = await SeededAsync();
        db.GuildChannels.Add(new GuildChannel
        {
            GuildId = 1, ChannelId = 100,
            Kind = GuildChannelKind.Text, Mode = TrackedChannelMode.Reward, PointsPerMessage = 3,
        });
        await db.SaveChangesAsync();

        var sut = new ActivityService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db), new RewardMultiplierService(db));

        await sut.RecordMessageAsync(1, channelId: 100, userId: 10, messageId: 1, DateTimeOffset.UtcNow);

        Assert.Equal(3, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance);
        Assert.Equal(1, await db.ActivityRecords.CountAsync());
    }
}
