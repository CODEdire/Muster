using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.Persistence.UnitTests.Queries;

public class TrackingQueriesTests
{
    private static TrackingSession Session(ulong guildId, ulong eventId, TrackingSessionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        GuildId = guildId,
        Source = TrackingSessionSource.DiscordScheduledEvent,
        ScheduledEventId = eventId,
        VoiceChannelId = 99,
        StartedAt = DateTimeOffset.UtcNow,
        Status = status,
    };

    [Fact]
    public async Task ActiveSessionForEvent_MatchesOnlyTheActiveBinding()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.TrackingSessions.AddRange(
            Session(1, 500, TrackingSessionStatus.Closed),   // same event, closed — ignored
            Session(1, 500, TrackingSessionStatus.Active));
        await db.SaveChangesAsync();

        Assert.True(await db.HasActiveSessionForEventAsync(1, 500));
        Assert.NotNull(await db.FindActiveSessionForEventAsync(1, 500));
        Assert.False(await db.HasActiveSessionForEventAsync(1, 999)); // no such event
    }

    [Fact]
    public async Task FindSessionWithAttendanceAsync_LoadsTheAttendanceAggregate()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var session = Session(1, 500, TrackingSessionStatus.Active);
        db.TrackingSessions.Add(session);
        db.VoiceAttendance.Add(new VoiceAttendance { TrackingSessionId = session.Id, UserId = 10, FirstJoinedAt = DateTimeOffset.UtcNow, TotalMinutes = 30 });
        await db.SaveChangesAsync();

        var loaded = await db.FindSessionWithAttendanceAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(30, Assert.Single(loaded!.Attendance).TotalMinutes);
    }
}
