using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Commands.Tracking;

namespace Muster.IntegrationTests;

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
    public async Task TrackStop_InvalidId_ReturnsError()
    {
        using var db = await SeededAsync();
        var sut = new TrackingCommandService(new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db)));

        var result = await sut.StopAsync(1, "not-a-guid");

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task TrackStop_UnknownSession_ReturnsError()
    {
        using var db = await SeededAsync();
        var sut = new TrackingCommandService(new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db)));

        var result = await sut.StopAsync(1, Guid.NewGuid().ToString());

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task TrackStart_ThenStop_Succeeds()
    {
        using var db = await SeededAsync();
        var sut = new TrackingCommandService(new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db)));

        var start = await sut.StartAsync(1, actorId: 5, voiceChannelId: 42, name: "Test op", channelName: "Voice", requireUnmuted: true, requireNotAlone: false);
        Assert.False(start.IsError);

        var session = await db.TrackingSessions.SingleAsync();
        var stop = await sut.StopAsync(1, session.Id.ToString());
        Assert.False(stop.IsError);
    }

    [Fact]
    public async Task Leaderboard_ReadsFromWalletCache()
    {
        using var db = await SeededAsync();
        var awards = new CurrencyService(db, new RecordingMessageBus());
        await awards.AwardPointsAsync(1, 10, 15, Muster.Domain.Enums.CurrencyLedgerSource.ManualAward, "s1", "r");

        var board = await new CurrencyReadService(db).GetSeasonLeaderboardAsync(1);

        var entry = Assert.Single(board);
        Assert.Equal(10ul, entry.UserId);
        Assert.Equal(15, entry.Total);
    }
}
