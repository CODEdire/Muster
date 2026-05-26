using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.Persistence.UnitTests.Queries;

public class ActivityQueriesTests
{
    [Fact]
    public async Task MessageRecordedAsync_TracksSeenMessageIds()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.ActivityRecords.Add(new ActivityRecord { GuildId = 1, ChannelId = 7, UserId = 10, Type = ActivityType.Message, Timestamp = DateTimeOffset.UtcNow, SourceMessageId = 555 });
        await db.SaveChangesAsync();

        Assert.True(await db.MessageRecordedAsync(555));
        Assert.False(await db.MessageRecordedAsync(556));
    }

    [Fact]
    public async Task FindDailyRollupAsync_MatchesTheFullScope()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var date = new DateOnly(2026, 5, 25);
        db.DailyActivityRollups.Add(new DailyActivityRollup { GuildId = 1, UserId = 10, ChannelId = 7, Date = date, MessageCount = 4 });
        await db.SaveChangesAsync();

        Assert.Equal(4, (await db.FindDailyRollupAsync(1, 10, 7, date))!.MessageCount);
        Assert.Null(await db.FindDailyRollupAsync(1, 10, 7, date.AddDays(1))); // different day
        Assert.Null(await db.FindDailyRollupAsync(1, 99, 7, date));            // different user
    }
}
