using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.Persistence.UnitTests.Queries;

public class SeasonQueriesTests
{
    [Fact]
    public async Task ActiveSeasonQueries_SelectTheOpenSeasonForTheGuild()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var active = new Season { Id = Guid.NewGuid(), GuildId = 1, Name = "S2", Status = SeasonStatus.Active };
        db.Seasons.AddRange(
            new Season { Id = Guid.NewGuid(), GuildId = 1, Name = "S1", Status = SeasonStatus.Archived },
            active,
            new Season { Id = Guid.NewGuid(), GuildId = 2, Name = "Other", Status = SeasonStatus.Active }); // different guild
        await db.SaveChangesAsync();

        Assert.Equal(active.Id, (await db.FindActiveSeasonAsync(1))!.Id);
        Assert.Equal(active.Id, await db.ActiveSeasonIdAsync(1));
        Assert.True(await db.HasActiveSeasonAsync(1));
    }

    [Fact]
    public async Task ActiveSeasonQueries_ReturnNothingWhenNoneOpen()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        db.Seasons.Add(new Season { Id = Guid.NewGuid(), GuildId = 1, Name = "S1", Status = SeasonStatus.Archived });
        await db.SaveChangesAsync();

        Assert.Null(await db.FindActiveSeasonAsync(1));
        Assert.Null(await db.ActiveSeasonIdAsync(1));
        Assert.False(await db.HasActiveSeasonAsync(1));
    }
}
