using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Persistence.Queries;
using Xunit;

namespace Muster.Persistence.UnitTests.Queries;

public class QuestQueriesTests
{
    [Fact]
    public async Task FindQuestAsync_LoadsTheParticipantAggregate()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var mission = GuildQuest.Create(new QuestDraft(1, 5, QuestOrigin.Guild, "Patrol", "", Guid.NewGuid(), 50));
        db.Quests.Add(mission);
        db.QuestParticipants.Add(QuestParticipant.NewClaim(mission.Id, 10));
        await db.SaveChangesAsync();

        var loaded = await db.FindQuestAsync(mission.Id);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Participants);
        Assert.Equal(10ul, loaded.Participants[0].UserId);
    }

    [Fact]
    public async Task FindQuestOriginAsync_ReturnsScalarOrigin_AndNullWhenAbsent()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var mission = GuildQuest.Create(new QuestDraft(1, 5, QuestOrigin.Player, "Escort", "", Guid.NewGuid(), 40));
        db.Quests.Add(mission);
        await db.SaveChangesAsync();

        Assert.Equal(QuestOrigin.Player, await db.FindQuestOriginAsync(mission.Id));
        Assert.Null(await db.FindQuestOriginAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task CountActiveQuestsByPosterAsync_CountsOnlyNonTerminalQuestsForThatPoster()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var coin = Guid.NewGuid();

        var open = GuildQuest.Create(new QuestDraft(1, 5, QuestOrigin.Guild, "A", "", coin, 10));
        var closed = GuildQuest.Create(new QuestDraft(1, 5, QuestOrigin.Guild, "B", "", coin, 10));
        closed.TransitionTo(QuestStatus.Closed);
        var otherPoster = GuildQuest.Create(new QuestDraft(1, 6, QuestOrigin.Guild, "C", "", coin, 10));
        var otherGuild = GuildQuest.Create(new QuestDraft(2, 5, QuestOrigin.Guild, "D", "", coin, 10));
        db.Quests.AddRange(open, closed, otherPoster, otherGuild);
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.CountActiveQuestsByPosterAsync(1, 5));
    }

    // Note: the list/sweep queries (ListQuestBoardAsync, ListScheduledDueAsync, ListExpiredPersonalQuestsAsync,
    // ListStaleSubmissionsAsync) either ORDER BY or compare DateTimeOffset, which the SQLite provider can't
    // translate (it stores DateTimeOffset as TEXT). Those belong in the SQL-Server (Testcontainers) suite.
}
