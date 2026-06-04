using Muster.Contracts;
using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Xunit;

namespace Muster.Persistence.UnitTests.Configurations;

/// <summary>
/// Schema constraints enforced by the database (not just app code) — verified on SQLite, which honours
/// unique, filtered, and non-unique indexes that the EF in-memory provider ignores entirely.
/// </summary>
public class ConstraintTests
{
    private static CurrencyLedgerEntry Entry(Guid currencyId, string? sourceId) => new()
    {
        GuildId = 1,
        UserId = 10,
        CurrencyId = currencyId,
        Amount = 5,
        SourceType = CurrencyLedgerSource.Quest,
        SourceId = sourceId,
        OccurredAt = DateTimeOffset.UtcNow,
        Reason = "t",
    };

    [Fact]
    public async Task CurrencyLedgerEntry_DuplicateSource_IsRejected()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var currencyId = Guid.NewGuid();

        db.CurrencyLedgerEntries.Add(Entry(currencyId, "quest:1:user:10"));
        await db.SaveChangesAsync();

        // Same (SourceType, SourceId) — the idempotency index must reject the second insert.
        db.CurrencyLedgerEntries.Add(Entry(currencyId, "quest:1:user:10"));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task CurrencyLedgerEntry_NullSource_AllowsDuplicates()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var currencyId = Guid.NewGuid();

        // The unique index is filtered to "SourceId IS NOT NULL", so connector entries (null source) stack.
        db.CurrencyLedgerEntries.Add(Entry(currencyId, null));
        db.CurrencyLedgerEntries.Add(Entry(currencyId, null));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.CurrencyLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Currency_CodeIsUniquePerGuild()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;

        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin" });
        await db.SaveChangesAsync();

        db.Currencies.Add(new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Dup" });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task QuestParticipant_AllowsMultiplePerMember()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var mission = GuildQuest.Create(new QuestDraft(1, 5, QuestOrigin.Guild, "Grind", "", Guid.NewGuid(), 10));
        db.Quests.Add(mission);

        // A member may hold several participations in one quest (repeat completions): the index is non-unique.
        db.QuestParticipants.Add(QuestParticipant.NewClaim(mission.Id, 10));
        db.QuestParticipants.Add(QuestParticipant.NewClaim(mission.Id, 10));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.QuestParticipants.CountAsync(p => p.QuestId == mission.Id && p.UserId == 10));
    }

    [Fact]
    public async Task Wallet_OneRowPerScope()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var currency = Guid.NewGuid();
        var season = Guid.NewGuid();

        // Non-null SeasonId: SQLite (unlike SQL Server) treats NULLs as distinct in a unique index, so a
        // null-scoped duplicate wouldn't trip it — the real-key collision is what the index must reject.
        db.Wallets.Add(new Wallet { GuildId = 1, UserId = 10, CurrencyId = currency, SeasonId = season, Balance = 5 });
        await db.SaveChangesAsync();

        db.Wallets.Add(new Wallet { GuildId = 1, UserId = 10, CurrencyId = currency, SeasonId = season, Balance = 9 });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task VoiceAttendance_OneRowPerMemberPerSession()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var session = new TrackingSession { Id = Guid.NewGuid(), GuildId = 1, VoiceChannelId = 99, StartedAt = DateTimeOffset.UtcNow };
        db.TrackingSessions.Add(session);

        db.VoiceAttendance.Add(new VoiceAttendance { TrackingSessionId = session.Id, UserId = 10, FirstJoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.VoiceAttendance.Add(new VoiceAttendance { TrackingSessionId = session.Id, UserId = 10, FirstJoinedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DailyActivityRollup_OneRowPerScope()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var date = new DateOnly(2026, 5, 25);

        db.DailyActivityRollups.Add(new DailyActivityRollup { GuildId = 1, UserId = 10, ChannelId = 7, Date = date, MessageCount = 3 });
        await db.SaveChangesAsync();

        db.DailyActivityRollups.Add(new DailyActivityRollup { GuildId = 1, UserId = 10, ChannelId = 7, Date = date, MessageCount = 1 });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ReactionParticipant_OneRowPerMemberPerMuster()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;
        var muster = new ReactionMuster { Id = Guid.NewGuid(), GuildId = 1, ChannelId = 7, Points = 5 };
        db.ReactionMusters.Add(muster);

        db.ReactionParticipants.Add(new ReactionParticipant { MusterId = muster.Id, UserId = 10, CheckedInAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.ReactionParticipants.Add(new ReactionParticipant { MusterId = muster.Id, UserId = 10, CheckedInAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ActivityRecord_DuplicateMessageId_IsRejected()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;

        db.ActivityRecords.Add(new ActivityRecord { GuildId = 1, ChannelId = 7, UserId = 10, Type = ActivityType.Message, Timestamp = DateTimeOffset.UtcNow, SourceMessageId = 555 });
        await db.SaveChangesAsync();

        // Gateway redelivery of the same message — the filtered unique index rejects the duplicate.
        db.ActivityRecords.Add(new ActivityRecord { GuildId = 1, ChannelId = 7, UserId = 10, Type = ActivityType.Message, Timestamp = DateTimeOffset.UtcNow, SourceMessageId = 555 });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ActivityRecord_NullMessageId_AllowsDuplicates()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;

        // Non-message activity carries no source message id; the index is filtered to "IS NOT NULL", so these stack.
        db.ActivityRecords.Add(new ActivityRecord { GuildId = 1, ChannelId = 7, UserId = 10, Type = ActivityType.Voice, Timestamp = DateTimeOffset.UtcNow, SourceMessageId = null });
        db.ActivityRecords.Add(new ActivityRecord { GuildId = 1, ChannelId = 7, UserId = 10, Type = ActivityType.Voice, Timestamp = DateTimeOffset.UtcNow, SourceMessageId = null });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.ActivityRecords.CountAsync());
    }
}
