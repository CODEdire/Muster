using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Tracking;
using Muster.IntegrationTests.TestSupport;
using Xunit;

namespace Muster.IntegrationTests;

public class BackgroundTrackingTests
{
    private const ulong Guild = 1;
    private const ulong Channel = 500;
    private static readonly DateTimeOffset T0 = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(Guild, "G", null);
        return db;
    }

    private static BackgroundTrackingService Sut(MusterDbContext db) =>
        new(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));

    private static async Task AddRewardVoiceAsync(
        MusterDbContext db, int rate = 2, int dailyCap = 0, bool requireUnmuted = true, bool requireNotAlone = true)
    {
        db.TrackedChannels.Add(new TrackedChannel
        {
            Id = Guid.NewGuid(),
            GuildId = Guild,
            ChannelId = Channel,
            Kind = TrackedChannelKind.Voice,
            Mode = TrackedChannelMode.Reward,
            PointsPerMinute = rate,
            DailyCapPoints = dailyCap,
            RequireUnmuted = requireUnmuted,
            RequireNotAlone = requireNotAlone,
        });
        await db.SaveChangesAsync();
    }

    private static IReadOnlyDictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> Roster(params VoiceMemberSnapshot[] members)
        => new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> { [Channel] = members };

    private static Task<long> BalanceAsync(MusterDbContext db, ulong userId)
        => db.Wallets.Where(w => w.UserId == userId).Select(w => w.Balance).SingleOrDefaultAsync();

    private static Task<int> VoiceMinutesAsync(MusterDbContext db, ulong userId)
        => db.DailyActivityRollups.Where(r => r.UserId == userId).Select(r => r.VoiceMinutes).SingleOrDefaultAsync();

    private static Task<int> SeasonMinutesAsync(MusterDbContext db, ulong userId)
        => db.SeasonParticipations.Where(p => p.UserId == userId).Select(p => p.VoiceMinutes).SingleOrDefaultAsync();

    private static async Task<Guid> AddActiveSeasonAsync(MusterDbContext db)
    {
        var id = Guid.NewGuid();
        db.Seasons.Add(new Season { Id = id, GuildId = Guild, Name = "S1", StartsAt = T0.AddDays(-1), Status = SeasonStatus.Active });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ActiveTime_AccruesEvenWhenNotRewarded_AndAttributesToSeason()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2);
        await AddActiveSeasonAsync(db);
        var sut = Sut(db);

        // Alone + muted → no reward, but active time still accrues (unguarded, overlaps everything).
        var roster = Roster(new VoiceMemberSnapshot(10, false, IsMuted: true, IsDeafened: false));
        await sut.ReconcileGuildAsync(Guild, roster, T0);
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddMinutes(10));

        Assert.Equal(0, await BalanceAsync(db, 10));       // no reward (alone + muted)
        Assert.Equal(10, await VoiceMinutesAsync(db, 10)); // active stats counted
        Assert.Equal(10, await SeasonMinutesAsync(db, 10)); // attributed to the active season
    }

    [Fact]
    public async Task BackgroundOptOut_ExcludesActiveAndReward()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2);
        await AddActiveSeasonAsync(db);
        db.GuildMembers.Add(new GuildMember { GuildId = Guild, UserId = 10, Tracking = TrackingChoice.BackgroundOut, JoinedAt = T0 });
        await db.SaveChangesAsync();
        var sut = Sut(db);

        var roster = Roster(new VoiceMemberSnapshot(10, false, false, false), new VoiceMemberSnapshot(20, false, false, false));
        await sut.ReconcileGuildAsync(Guild, roster, T0);
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddMinutes(10));

        Assert.Equal(0, await BalanceAsync(db, 10));       // opted out: no reward
        Assert.Equal(0, await VoiceMinutesAsync(db, 10));  // opted out: no stats
        Assert.Equal(20, await BalanceAsync(db, 20));      // peer (Default) still rewarded
    }

    [Fact]
    public async Task GuildOptIn_DefaultMember_NotBackgroundTracked()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2);
        var guild = await db.FindGuildAsync(Guild);
        guild!.Settings.BackgroundTrackingOptIn = true; // opt-in guild
        guild.Settings = guild.Settings;
        await db.SaveChangesAsync();
        var sut = Sut(db);

        var roster = Roster(new VoiceMemberSnapshot(10, false, false, false), new VoiceMemberSnapshot(20, false, false, false));
        await sut.ReconcileGuildAsync(Guild, roster, T0);
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddMinutes(10));

        Assert.Equal(0, await BalanceAsync(db, 10));      // Default member, opt-in guild → not tracked
        Assert.Equal(0, await VoiceMinutesAsync(db, 10));
    }

    [Fact]
    public async Task TwoUnmutedPresent_AccruesAndAwardsMinutes()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2);
        var sut = Sut(db);

        var roster = Roster(new VoiceMemberSnapshot(10, false, false, false), new VoiceMemberSnapshot(20, false, false, false));
        await sut.ReconcileGuildAsync(Guild, roster, T0);                       // opens segments
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddMinutes(10));        // flush 10 min

        Assert.Equal(20, await BalanceAsync(db, 10)); // 10 min * 2
        Assert.Equal(20, await BalanceAsync(db, 20));
    }

    [Fact]
    public async Task AloneInChannel_NotRewarded_WhenRequireNotAlone()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, requireNotAlone: true);
        var sut = Sut(db);

        var roster = Roster(new VoiceMemberSnapshot(10, false, false, false));
        await sut.ReconcileGuildAsync(Guild, roster, T0);
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddMinutes(10));

        Assert.Equal(0, await BalanceAsync(db, 10));
    }

    [Fact]
    public async Task MutedMember_NotRewarded_WhenRequireUnmuted()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2, requireUnmuted: true);
        var sut = Sut(db);

        var roster = Roster(new VoiceMemberSnapshot(10, false, IsMuted: true, IsDeafened: false), new VoiceMemberSnapshot(20, false, false, false));
        await sut.ReconcileGuildAsync(Guild, roster, T0);
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddMinutes(10));

        Assert.Equal(0, await BalanceAsync(db, 10));  // muted: no reward
        Assert.Equal(20, await BalanceAsync(db, 20)); // unmuted peer keeps earning
    }

    [Fact]
    public async Task ActiveSession_SuppressesBackground()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2);
        db.TrackingSessions.Add(new TrackingSession
        {
            Id = Guid.NewGuid(),
            GuildId = Guild,
            Source = TrackingSessionSource.Manual,
            VoiceChannelId = Channel,
            StartedAt = T0,
            Status = TrackingSessionStatus.Active,
            OpenedBy = 1,
        });
        await db.SaveChangesAsync();
        var sut = Sut(db);

        var roster = Roster(new VoiceMemberSnapshot(10, false, false, false), new VoiceMemberSnapshot(20, false, false, false));
        await sut.ReconcileGuildAsync(Guild, roster, T0);
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddMinutes(10));

        Assert.Equal(0, await BalanceAsync(db, 10)); // session owns the channel
        Assert.Equal(0, await BalanceAsync(db, 20));
    }

    [Fact]
    public async Task DailyCap_ClampsAward()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2, dailyCap: 10);
        var sut = Sut(db);

        var roster = Roster(new VoiceMemberSnapshot(10, false, false, false), new VoiceMemberSnapshot(20, false, false, false));
        await sut.ReconcileGuildAsync(Guild, roster, T0);
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddMinutes(30)); // 60 pts uncapped → clamped to 10

        Assert.Equal(10, await BalanceAsync(db, 10));
    }

    [Fact]
    public async Task LargeGap_ClampsToOneFlushWindow()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2);
        var sut = Sut(db);

        var roster = Roster(new VoiceMemberSnapshot(10, false, false, false), new VoiceMemberSnapshot(20, false, false, false));
        await sut.ReconcileGuildAsync(Guild, roster, T0);
        // A 3-hour jump (e.g. a gateway reconnect with a stale watermark) must not pay 3 hours.
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddHours(3));

        Assert.Equal(20, await BalanceAsync(db, 10)); // clamped to MaxFlushMinutes (10) * 2
    }

    [Fact]
    public async Task VoidOpenSegments_PreventsRestartOverAward()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2);
        var sut = Sut(db);

        var roster = Roster(new VoiceMemberSnapshot(10, false, false, false), new VoiceMemberSnapshot(20, false, false, false));
        await sut.ReconcileGuildAsync(Guild, roster, T0); // opens segments

        await sut.VoidOpenSegmentsAsync(); // simulate bot restart

        // First reconcile after restart reopens the segment fresh — the pre-restart window isn't credited.
        await sut.ReconcileGuildAsync(Guild, roster, T0.AddHours(3));

        Assert.Equal(0, await BalanceAsync(db, 10));
    }

    [Fact]
    public async Task LeavingChannel_FlushesAccruedTime()
    {
        using var db = await SeededAsync();
        await AddRewardVoiceAsync(db, rate: 2);
        var sut = Sut(db);

        var both = Roster(new VoiceMemberSnapshot(10, false, false, false), new VoiceMemberSnapshot(20, false, false, false));
        await sut.ReconcileGuildAsync(Guild, both, T0);
        // User 10 leaves at +5 min; 20 stays so the channel still isn't "alone".
        await sut.ReconcileGuildAsync(Guild, Roster(new VoiceMemberSnapshot(20, false, false, false)), T0.AddMinutes(5));

        Assert.Equal(10, await BalanceAsync(db, 10)); // 5 min * 2, settled on leave
    }
}
