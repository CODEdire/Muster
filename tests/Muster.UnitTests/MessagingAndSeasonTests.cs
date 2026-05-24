using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Messaging;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

public class MessagingAndSeasonTests
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
    public async Task Season_Start_ArchivesPrevious_AndOpensNew()
    {
        using var db = await SeededAsync();
        var sut = new SeasonService(db);

        // Provisioning already created "Season 1".
        var season2 = await sut.StartAsync(1, "Season 2");

        Assert.Equal("Season 2", season2.Name);
        Assert.Equal(1, await db.Seasons.CountAsync(s => s.Status == SeasonStatus.Active));
        Assert.Equal(1, await db.Seasons.CountAsync(s => s.Status == SeasonStatus.Archived));
    }

    [Fact]
    public async Task Season_End_NoActive_ReturnsError()
    {
        using var db = await SeededAsync();
        var commands = new SeasonCommandService(new SeasonService(db));

        Assert.False((await commands.EndAsync(1)).IsError);   // ends the seeded season
        Assert.True((await commands.EndAsync(1)).IsError);     // nothing active now
    }

    [Fact]
    public async Task TrackingClose_UsesGuildConfiguredPointsPerMinute()
    {
        using var db = await SeededAsync();
        var guild = await db.Guilds.SingleAsync();
        guild.Settings.PointsPerVoiceMinute = 3;
        await db.SaveChangesAsync();

        var sessions = new TrackingSessionService(db, new AwardService(db), new GuildAuthorizationService(db));
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var joined = DateTimeOffset.UtcNow.AddMinutes(-10);
        await sessions.ProcessVoiceStateAsync(1, 10, currentChannelId: 500, at: joined);
        await sessions.CloseAsync(session.Id, at: joined.AddMinutes(10)); // no explicit rate -> use config (3)

        Assert.Equal(30, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance); // 10 min * 3
    }

    [Fact]
    public async Task AwardCurrencyHandler_WritesLedger_AndReturnsEvent()
    {
        using var db = await SeededAsync();
        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var awards = new AwardService(db);

        var evt = await AwardCurrencyHandler.Handle(
            new AwardCurrency(1, 10, points.Id, 40, nameof(LedgerSourceType.ManualAward), "src-1", "reason"),
            awards, CancellationToken.None);

        Assert.Equal(40, evt.Amount);
        Assert.Equal(10ul, evt.UserId);
        Assert.Equal(1, await db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task AdjustCurrencyBalanceHandler_MintsConnectorCurrency()
    {
        using var db = await SeededAsync();
        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var awards = new AwardService(db);

        var evt = await AdjustCurrencyBalanceHandler.Handle(
            new AdjustCurrencyBalance(1, 10, points.Id, 100, "loot drop"), awards, CancellationToken.None);

        Assert.Equal(100, evt.Amount);
        Assert.Equal(LedgerSourceType.Connector, (await db.LedgerEntries.SingleAsync()).SourceType);
    }
}
