using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Seasons;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Commands.Seasons;

namespace Muster.IntegrationTests;

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
        guild.Settings.ApplyAfkGuardsToSessions = false; // single-user accrual test
        await db.SaveChangesAsync();

        var sessions = new TrackingSessionService(db, new CurrencyService(db, new RecordingMessageBus()), new GuildAuthorizationService(db));
        var session = await sessions.OpenManualAsync(1, voiceChannelId: 500, openedBy: 5);

        var joined = DateTimeOffset.UtcNow.AddMinutes(-10);
        var roster = new Dictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> { [500] = new[] { new VoiceMemberSnapshot(10, false, false, false) } };
        await sessions.ReconcileSessionsAsync(1, roster, joined);
        await sessions.CloseAsync(session.Id, at: joined.AddMinutes(10)); // no explicit rate -> use config (3)

        Assert.Equal(30, (await db.Wallets.SingleAsync(w => w.UserId == 10)).Balance); // 10 min * 3
    }
}
