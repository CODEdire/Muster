using Muster.Contracts;
using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Xunit;

namespace Muster.Persistence.UnitTests.Configurations;

/// <summary>
/// Child rows are deleted with their aggregate root — the FK cascade is owned by the database (SQLite
/// honours it via PRAGMA foreign_keys=ON, which EF enables), not just by EF change-tracking.
/// </summary>
public class CascadeTests
{
    [Fact]
    public async Task DeletingMission_CascadesToParticipants()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var mission = GuildQuest.Create(new QuestDraft(1, 5, QuestOrigin.Guild, "Patrol", "", Guid.NewGuid(), 50));
        db.Quests.Add(mission);
        db.QuestParticipants.Add(QuestParticipant.NewClaim(mission.Id, 10));
        db.QuestParticipants.Add(QuestParticipant.NewClaim(mission.Id, 20));
        await db.SaveChangesAsync();

        db.Quests.Remove(mission);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.QuestParticipants.CountAsync());
    }

    [Fact]
    public async Task DeletingTrackingSession_CascadesToAttendance()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var session = new TrackingSession { Id = Guid.NewGuid(), GuildId = 1, VoiceChannelId = 99, StartedAt = DateTimeOffset.UtcNow };
        db.TrackingSessions.Add(session);
        db.VoiceAttendance.Add(new VoiceAttendance { TrackingSessionId = session.Id, UserId = 10, FirstJoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.TrackingSessions.Remove(session);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.VoiceAttendance.CountAsync());
    }

    [Fact]
    public async Task DeletingMuster_CascadesToParticipants()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var muster = new ReactionMuster { Id = Guid.NewGuid(), GuildId = 1, ChannelId = 7, MessageId = 42, CurrencyId = Guid.NewGuid(), RewardAmount = 5 };
        db.ReactionMusters.Add(muster);
        db.ReactionParticipants.Add(new ReactionParticipant { MusterId = muster.Id, UserId = 10, Emoji = "✅", ReactedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.ReactionMusters.Remove(muster);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.ReactionParticipants.CountAsync());
    }
}
