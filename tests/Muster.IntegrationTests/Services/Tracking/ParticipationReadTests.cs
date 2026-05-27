using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Tracking;
using Xunit;

namespace Muster.IntegrationTests;

public class ParticipationReadTests
{
    private const ulong Guild = 1;
    private static readonly DateOnly D1 = new(2026, 5, 20);

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

    private static void AddRollup(MusterDbContext db, ulong userId, ulong channelId, int voice, int messages)
        => db.DailyActivityRollups.Add(new DailyActivityRollup
        {
            GuildId = Guild, UserId = userId, ChannelId = channelId, Date = D1,
            VoiceMinutes = voice, MessageCount = messages,
        });

    private static void AddLedger(MusterDbContext db, ulong userId, CurrencyLedgerSource source, long amount)
        => db.CurrencyLedgerEntries.Add(new CurrencyLedgerEntry
        {
            GuildId = Guild, UserId = userId, CurrencyId = Guid.NewGuid(), Amount = amount,
            SourceType = source, OccurredAt = new DateTimeOffset(D1.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero),
            Reason = "test",
        });

    [Fact]
    public async Task VoiceLeaderboard_Season_OrdersByMinutesDesc()
    {
        using var db = await SeededAsync();
        var seasonId = (await db.ActiveSeasonIdAsync(Guild))!.Value; // provisioning seeds the active season
        db.SeasonParticipations.Add(new SeasonParticipation { Id = Guid.NewGuid(), GuildId = Guild, UserId = 10, SeasonId = seasonId, VoiceMinutes = 40 });
        db.SeasonParticipations.Add(new SeasonParticipation { Id = Guid.NewGuid(), GuildId = Guild, UserId = 20, SeasonId = seasonId, VoiceMinutes = 100 });
        await db.SaveChangesAsync();

        var board = await new ParticipationReadService(db).VoiceLeaderboardAsync(Guild, 10);

        Assert.Equal(2, board.Count);
        Assert.Equal(20ul, board[0].UserId);
        Assert.Equal(100, board[0].VoiceMinutes);
        Assert.Equal(10ul, board[1].UserId);
    }

    [Fact]
    public async Task VoiceLeaderboard_NoSeason_FallsBackToAllTimeRollups()
    {
        using var db = await SeededAsync();
        // No active season → all-time fallback. Archive the season provisioning seeded.
        var season = await db.FindActiveSeasonAsync(Guild);
        season!.Status = SeasonStatus.Archived;
        AddRollup(db, userId: 10, channelId: 100, voice: 15, messages: 0);
        AddRollup(db, userId: 10, channelId: 200, voice: 15, messages: 0); // two channels sum to 30
        AddRollup(db, userId: 20, channelId: 100, voice: 5, messages: 0);
        await db.SaveChangesAsync();

        var board = await new ParticipationReadService(db).VoiceLeaderboardAsync(Guild, 10);

        Assert.Equal(10ul, board[0].UserId);
        Assert.Equal(30, board[0].VoiceMinutes);
        Assert.Equal(20ul, board[1].UserId);
    }

    [Fact]
    public async Task Report_AggregatesVoiceMessagesAndPointsBySource()
    {
        using var db = await SeededAsync();
        AddRollup(db, userId: 10, channelId: 100, voice: 30, messages: 5);
        AddRollup(db, userId: 10, channelId: 200, voice: 10, messages: 2);
        AddLedger(db, userId: 10, CurrencyLedgerSource.TrackingSession, 20);
        AddLedger(db, userId: 10, CurrencyLedgerSource.Background, 5);
        AddLedger(db, userId: 10, CurrencyLedgerSource.Event, 75);
        AddLedger(db, userId: 10, CurrencyLedgerSource.Adjustment, 999); // not a participation source → excluded
        await db.SaveChangesAsync();

        var rows = await new ParticipationReadService(db).ReportAsync(Guild, D1, D1);

        var row = Assert.Single(rows);
        Assert.Equal(10ul, row.UserId);
        Assert.Equal(40, row.VoiceMinutes);
        Assert.Equal(7, row.MessageCount);
        Assert.Equal(20, row.SessionPoints);
        Assert.Equal(5, row.BackgroundPoints);
        Assert.Equal(75, row.EventPoints);
        Assert.Equal(0, row.QuestPoints);
        Assert.Equal(0, row.MusterPoints);
    }

    [Fact]
    public async Task ActiveSessions_ReportsAttendeesAndPresentNow()
    {
        using var db = await SeededAsync();
        var sid = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.TrackingSessions.Add(new TrackingSession { Id = sid, GuildId = Guild, Source = TrackingSessionSource.Manual, VoiceChannelId = 500, StartedAt = now, Status = TrackingSessionStatus.Active, OpenedBy = 1 });
        db.VoiceAttendance.Add(new VoiceAttendance { Id = Guid.NewGuid(), TrackingSessionId = sid, UserId = 10, FirstJoinedAt = now, OpenSegmentStart = now }); // present
        db.VoiceAttendance.Add(new VoiceAttendance { Id = Guid.NewGuid(), TrackingSessionId = sid, UserId = 20, FirstJoinedAt = now, OpenSegmentStart = null }); // left
        await db.SaveChangesAsync();

        var active = await new ParticipationReadService(db).ActiveSessionsAsync(Guild);

        var view = Assert.Single(active);
        Assert.Equal(2, view.Attendees);
        Assert.Equal(1, view.PresentNow);
    }

    [Fact]
    public async Task RecentSessions_ReportsClosedWithMinutes()
    {
        using var db = await SeededAsync();
        var sid = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        db.TrackingSessions.Add(new TrackingSession { Id = sid, GuildId = Guild, Source = TrackingSessionSource.Manual, VoiceChannelId = 500, StartedAt = start, EndedAt = start.AddMinutes(30), Status = TrackingSessionStatus.Closed, OpenedBy = 1 });
        db.VoiceAttendance.Add(new VoiceAttendance { Id = Guid.NewGuid(), TrackingSessionId = sid, UserId = 10, FirstJoinedAt = start, TotalMinutes = 30 });
        await db.SaveChangesAsync();

        var recent = await new ParticipationReadService(db).RecentSessionsAsync(Guild);

        var view = Assert.Single(recent);
        Assert.Equal(1, view.Attendees);
        Assert.Equal(30, view.TotalMinutes);
    }

    [Fact]
    public async Task MemberVoiceStats_SeasonAllTimeAndRank()
    {
        using var db = await SeededAsync();
        var seasonId = (await db.ActiveSeasonIdAsync(Guild))!.Value;
        db.SeasonParticipations.Add(new SeasonParticipation { Id = Guid.NewGuid(), GuildId = Guild, UserId = 10, SeasonId = seasonId, VoiceMinutes = 40 });
        db.SeasonParticipations.Add(new SeasonParticipation { Id = Guid.NewGuid(), GuildId = Guild, UserId = 20, SeasonId = seasonId, VoiceMinutes = 100 });
        AddRollup(db, userId: 10, channelId: 100, voice: 50, messages: 0);
        await db.SaveChangesAsync();

        var stats = await new ParticipationReadService(db).MemberVoiceStatsAsync(Guild, 10);

        Assert.Equal(40, stats.SeasonMinutes);
        Assert.Equal(50, stats.AllTimeMinutes);
        Assert.Equal(2, stats.SeasonRank); // one member ahead
    }

    [Fact]
    public async Task Report_ExcludesEntriesOutsideRange()
    {
        using var db = await SeededAsync();
        AddRollup(db, userId: 10, channelId: 100, voice: 30, messages: 5);
        await db.SaveChangesAsync();

        // Range entirely after D1 → no rows.
        var rows = await new ParticipationReadService(db).ReportAsync(Guild, D1.AddDays(1), D1.AddDays(2));

        Assert.Empty(rows);
    }
}
