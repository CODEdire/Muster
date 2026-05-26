using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;

namespace Muster.IntegrationTests;

public class WebMemberServiceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task GetMember_ReturnsDisplayName_Wallets_AndHistory()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        await new MemberSyncService(db).UpsertAsync(1, 10, "alice", "Alice", null, nickname: "Ace");

        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var awards = new CurrencyService(db, new NullCurrencyEventSink());
        await awards.AwardAsync(1, 10, points.Id, 30, LedgerSourceType.ManualAward, "a", "great work");
        await awards.AwardAsync(1, 10, points.Id, 20, LedgerSourceType.Muster, "m", "check-in");

        var sut = new WebMemberService(db, new ScoreQueryService(db));
        var detail = await sut.GetAsync(1, 10);

        Assert.Equal("Ace", detail.DisplayName); // nickname preferred
        Assert.Equal(50, detail.Wallets.Single(w => w.CurrencyCode == "POINTS").Balance);
        Assert.Equal(2, detail.History.Count);
        Assert.Equal("check-in", detail.History[0].Reason); // newest first
    }
}
