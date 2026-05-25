using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

public class WebGuildServiceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static WebGuildService Sut(MusterDbContext db)
        => new(db, new GuildAuthorizationService(db), new ScoreQueryService(db));

    [Fact]
    public async Task GetGuildsForUser_ReturnsMembershipsAndOwned_WithAdminFlag()
    {
        using var db = NewDb();
        var prov = new GuildProvisioningService(db);
        await prov.EnsureGuildAsync(1, "Owned", null, ownerId: 10);
        await prov.EnsureGuildAsync(2, "Member", null, ownerId: 999);
        await prov.EnsureGuildAsync(3, "Unrelated", null, ownerId: 999);
        await new MemberSyncService(db).UpsertAsync(2, 10, "u", null, null);

        var guilds = await Sut(db).GetGuildsForUserAsync(10);

        Assert.Equal(2, guilds.Count);
        Assert.True(guilds.Single(g => g.GuildId == 1).IsAdmin);   // owner
        Assert.False(guilds.Single(g => g.GuildId == 2).IsAdmin);  // plain member
        Assert.DoesNotContain(guilds, g => g.GuildId == 3);
    }

    [Fact]
    public async Task CanViewGuild_TrueForOwnerAndMember_FalseOtherwise()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 10);
        await new MemberSyncService(db).UpsertAsync(1, 20, "u", null, null);

        var sut = Sut(db);
        Assert.True(await sut.CanViewGuildAsync(1, 10));  // owner
        Assert.True(await sut.CanViewGuildAsync(1, 20));  // member
        Assert.False(await sut.CanViewGuildAsync(1, 30)); // neither
    }

    [Fact]
    public async Task GetLeaderboard_RanksByPoints_WithDisplayNames()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var awards = new AwardService(db);
        var sync = new MemberSyncService(db);

        await sync.UpsertAsync(1, 10, "alice", "Alice", null);
        await sync.UpsertAsync(1, 20, "bob", null, null);
        await awards.AwardAsync(1, 10, points.Id, 30, Muster.Domain.Enums.LedgerSourceType.ManualAward, "a", "r");
        await awards.AwardAsync(1, 20, points.Id, 70, Muster.Domain.Enums.LedgerSourceType.ManualAward, "b", "r");

        var board = await Sut(db).GetLeaderboardAsync(1);

        Assert.Equal(2, board.Count);
        Assert.Equal(20ul, board[0].UserId);          // 70 points first
        Assert.Equal("bob", board[0].DisplayName);
        Assert.Equal("Alice", board[1].DisplayName);  // GlobalName preferred over username
    }
}
