using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Xunit;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Commands.Ledger;
using Muster.Infrastructure.Commands.Tracking;

namespace Muster.UnitTests;

/// <summary>
/// Exercises the platform-independent command layer directly — no Discord required. This is the
/// abstraction that lets us test every command's validation and formatting without a gateway.
/// </summary>
public class CommandServiceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync(ulong guildId = 1)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(guildId, "G", null);
        return db;
    }

    [Fact]
    public async Task Award_RejectsNonPositiveAmount_WithoutWriting()
    {
        using var db = await SeededAsync();
        var sut = new AwardCommandService(new ManualAwardService(db, new AwardService(db)));

        var result = await sut.AwardPointsAsync(1, actorId: 5, memberId: 10, amount: 0, reason: "x");

        Assert.True(result.IsError);
        Assert.Equal(0, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Award_Succeeds_WritesLedger_AndMentionsMember()
    {
        using var db = await SeededAsync();
        var sut = new AwardCommandService(new ManualAwardService(db, new AwardService(db)));

        var result = await sut.AwardPointsAsync(1, actorId: 5, memberId: 10, amount: 25, reason: "great work");

        Assert.False(result.IsError);
        Assert.Contains("<@10>", result.Message);
        Assert.Contains("25", result.Message);
        Assert.Equal(1, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task TrackStop_InvalidId_ReturnsError()
    {
        using var db = await SeededAsync();
        var sut = new TrackingCommandService(new TrackingSessionService(db, new AwardService(db), new GuildAuthorizationService(db)));

        var result = await sut.StopAsync(1, "not-a-guid");

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task TrackStop_UnknownSession_ReturnsError()
    {
        using var db = await SeededAsync();
        var sut = new TrackingCommandService(new TrackingSessionService(db, new AwardService(db), new GuildAuthorizationService(db)));

        var result = await sut.StopAsync(1, Guid.NewGuid().ToString());

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task TrackStart_ThenStop_Succeeds()
    {
        using var db = await SeededAsync();
        var sut = new TrackingCommandService(new TrackingSessionService(db, new AwardService(db), new GuildAuthorizationService(db)));

        var start = await sut.StartAsync(1, actorId: 5, voiceChannelId: 42);
        Assert.False(start.IsError);

        var session = await db.TrackingSessions.SingleAsync();
        var stop = await sut.StopAsync(1, session.Id.ToString());
        Assert.False(stop.IsError);
    }

    [Fact]
    public void FormatLeaderboard_RendersRankedMentions()
    {
        var text = ScoreCommandService.FormatLeaderboard(
        [
            new LeaderboardEntry(10, 50),
            new LeaderboardEntry(20, 40),
        ]);

        Assert.Contains("1. <@10> — **50**", text);
        Assert.Contains("2. <@20> — **40**", text);
    }

    [Fact]
    public void FormatLeaderboard_Empty_ReturnsFriendlyMessage()
    {
        Assert.Equal("No scores yet this season.", ScoreCommandService.FormatLeaderboard([]));
    }

    [Fact]
    public async Task Wallet_FormatsSeededPointsBalance()
    {
        using var db = await SeededAsync();
        var awards = new AwardService(db);
        await awards.AwardPointsAsync(1, 10, 15, Muster.Domain.Enums.LedgerSourceType.ManualAward, "s1", "r");

        var sut = new ScoreCommandService(new ScoreQueryService(db));
        var result = await sut.WalletAsync(1, 10);

        Assert.False(result.IsError);
        Assert.Contains("Points: **15**", result.Message);
    }
}
