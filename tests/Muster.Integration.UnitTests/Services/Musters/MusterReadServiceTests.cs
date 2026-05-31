using Microsoft.EntityFrameworkCore;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Musters;
using Muster.Persistence;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>Read-side projections for the web muster surfaces — the participant board (cards), KPIs, and the manage
/// grid list. In-memory DB; pins the shapes the UI relies on (active-only, your-checked-in flag, session stitching).</summary>
public class MusterReadServiceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-read-{Guid.NewGuid()}")
            .Options);

    private static ReactionMuster Muster(MusterStatus status, ulong createdBy = 7, Guid? coinCcy = null,
        long points = 0, long coins = 0, int? minCheckIns = null) =>
        new()
        {
            Id = Guid.NewGuid(), GuildId = 1, Prompt = status.ToString(), Status = status, CreatedBy = createdBy,
            CoinCurrencyId = coinCcy, Points = points, Coins = coins, MinCheckIns = minCheckIns,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static void CheckIn(ReactionMuster m, params ulong[] userIds)
    {
        foreach (var u in userIds)
        {
            m.Participants.Add(new ReactionParticipant { Id = Guid.NewGuid(), MusterId = m.Id, UserId = u, CheckedInAt = DateTimeOffset.UtcNow });
        }
    }

    [Fact]
    public async Task ActiveCards_ReturnsOnlyActive_FlagsCheckedIn_AndStitchesSessions()
    {
        using var db = NewDb();
        var ccy = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "RSI", Name = "Republic", IsSpendable = true };
        var session = new TrackingSession { Id = Guid.NewGuid(), GuildId = 1, Name = "Op Night", Status = TrackingSessionStatus.Active };
        db.Currencies.Add(ccy);
        db.TrackingSessions.Add(session);

        var open = Muster(MusterStatus.Open, coinCcy: ccy.Id, points: 10, coins: 5);
        CheckIn(open, 50);
        open.SessionLinks.Add(new MusterSessionLink { MusterId = open.Id, SessionId = session.Id });
        var locked = Muster(MusterStatus.Locked);
        var closed = Muster(MusterStatus.Closed);
        db.ReactionMusters.AddRange(open, locked, closed);
        await db.SaveChangesAsync();

        var svc = new MusterReadService(db);
        var cards = await svc.ActiveCardsAsync(1, userId: 50);

        Assert.Equal(2, cards.Count); // open + locked, never the closed one
        var openCard = cards.Single(c => c.Id == open.Id);
        Assert.True(openCard.YouCheckedIn);
        Assert.Equal("RSI", openCard.CoinCode);
        Assert.Equal("Op Night", Assert.Single(openCard.Sessions).Name);
        Assert.False(cards.Single(c => c.Id == locked.Id).YouCheckedIn);

        // A different member isn't flagged as checked-in.
        var others = await svc.ActiveCardsAsync(1, userId: 999);
        Assert.False(others.Single(c => c.Id == open.Id).YouCheckedIn);
    }

    [Fact]
    public async Task Kpis_CountByStatus_CheckedInOnOpen_AndLinked()
    {
        using var db = NewDb();
        var session = new TrackingSession { Id = Guid.NewGuid(), GuildId = 1, Name = "S", Status = TrackingSessionStatus.Active };
        db.TrackingSessions.Add(session);

        var open = Muster(MusterStatus.Open);
        CheckIn(open, 1, 2, 3);
        open.SessionLinks.Add(new MusterSessionLink { MusterId = open.Id, SessionId = session.Id });
        db.ReactionMusters.AddRange(open, Muster(MusterStatus.Locked), Muster(MusterStatus.Closed), Muster(MusterStatus.Open));
        await db.SaveChangesAsync();

        var kpis = await new MusterReadService(db).GetKpisAsync(1);

        Assert.Equal(2, kpis.Open);
        Assert.Equal(1, kpis.Locked);
        Assert.Equal(3, kpis.CheckedInOnOpen); // participants across Open musters
        Assert.Equal(1, kpis.Linked);          // only the linked, still-active muster
        Assert.Equal(4, kpis.Total);
    }

    [Fact]
    public async Task List_IncludeClosed_TogglesTerminalRows_AndCarriesSessions()
    {
        using var db = NewDb();
        var session = new TrackingSession { Id = Guid.NewGuid(), GuildId = 1, Name = "Linked Op", Status = TrackingSessionStatus.Active };
        db.TrackingSessions.Add(session);

        var open = Muster(MusterStatus.Open, minCheckIns: 2);
        open.SessionLinks.Add(new MusterSessionLink { MusterId = open.Id, SessionId = session.Id });
        db.ReactionMusters.AddRange(open, Muster(MusterStatus.Closed));
        await db.SaveChangesAsync();

        var svc = new MusterReadService(db);

        var openOnly = await svc.ListAsync(1, includeClosed: false);
        Assert.Equal(open.Id, Assert.Single(openOnly).Id);

        var all = await svc.ListAsync(1, includeClosed: true);
        Assert.Equal(2, all.Count);
        var openRow = all.Single(m => m.Id == open.Id);
        Assert.Equal(2, openRow.MinCheckIns);
        Assert.Equal("Linked Op", Assert.Single(openRow.Sessions).Name);
    }
}
