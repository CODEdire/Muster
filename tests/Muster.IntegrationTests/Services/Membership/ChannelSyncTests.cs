using Microsoft.EntityFrameworkCore;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Membership;
using Xunit;

namespace Muster.IntegrationTests;

public class ChannelSyncTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task Upsert_CreatesThenUpdatesInPlace()
    {
        using var db = NewDb();
        var sut = new ChannelSyncService(db);

        await sut.UpsertAsync(1, 10, "general", GuildChannelKind.Text, position: 2);
        await sut.UpsertAsync(1, 10, "general-renamed", GuildChannelKind.Text, position: 5);

        var channel = await db.GuildChannels.SingleAsync();
        Assert.Equal("general-renamed", channel.Name);
        Assert.Equal(5, channel.Position);
        Assert.Equal(GuildChannelKind.Text, channel.Kind);
    }

    [Fact]
    public async Task SyncAll_AddsMissing_AndRemovesStale()
    {
        using var db = NewDb();
        var sut = new ChannelSyncService(db);

        await sut.SyncAllAsync(1,
        [
            (10, "general", GuildChannelKind.Text, 0),
            (20, "Voice 1", GuildChannelKind.Voice, 1),
        ]);

        // Channel 10 dropped, 20 kept (renamed), 30 added.
        var kept = await sut.SyncAllAsync(1,
        [
            (20, "Voice One", GuildChannelKind.Voice, 1),
            (30, "lobby", GuildChannelKind.Text, 2),
        ]);

        Assert.Equal(2, kept);
        var ids = await db.GuildChannels.Select(c => c.ChannelId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new ulong[] { 20, 30 }, ids);
        var voice = await db.FindChannelAsync(1, 20);
        Assert.Equal("Voice One", voice!.Name);
    }

    [Fact]
    public async Task Remove_DeletesChannel()
    {
        using var db = NewDb();
        var sut = new ChannelSyncService(db);

        await sut.UpsertAsync(1, 10, "general", GuildChannelKind.Text, null);
        await sut.RemoveAsync(1, 10);

        Assert.Equal(0, await db.GuildChannels.CountAsync());
    }

    [Fact]
    public async Task SyncAll_SoftDeletesConfigured_HardRemovesUnconfigured()
    {
        using var db = NewDb();
        var sut = new ChannelSyncService(db);

        await sut.SyncAllAsync(1,
        [
            (10, "tracked", GuildChannelKind.Voice, 0),
            (20, "plain", GuildChannelKind.Text, 1),
        ]);
        // Configure channel 10 (it now carries tracking config).
        var tracked = await db.FindChannelAsync(1, 10);
        tracked!.Mode = TrackedChannelMode.Reward;
        await db.SaveChangesAsync();

        // Both channels vanish from Discord.
        var at = DateTimeOffset.UtcNow;
        await sut.SyncAllAsync(1, [], at: at);

        // Configured one is soft-deleted (kept); plain one is gone.
        var stillThere = await db.FindChannelAsync(1, 10);
        Assert.NotNull(stillThere);
        Assert.Equal(at, stillThere!.DeletedAt);
        Assert.Equal(TrackedChannelMode.Reward, stillThere.Mode); // config preserved
        Assert.Null(await db.FindChannelAsync(1, 20));

        // Pickers exclude soft-deleted; the tracked-config list keeps it (so admins can clean it up).
        Assert.Empty(await db.ListChannelsByKindAsync(1, GuildChannelKind.Voice));
        Assert.Contains(await db.ListTrackedChannelsAsync(1), c => c.ChannelId == 10);
    }

    [Fact]
    public async Task SyncAll_Reappearing_ClearsSoftDelete()
    {
        using var db = NewDb();
        var sut = new ChannelSyncService(db);

        await sut.SyncAllAsync(1, [(10, "v", GuildChannelKind.Voice, 0)]);
        var c = await db.FindChannelAsync(1, 10);
        c!.Mode = TrackedChannelMode.Reward;
        await db.SaveChangesAsync();

        await sut.SyncAllAsync(1, []);                                   // gone -> soft-deleted
        Assert.NotNull((await db.FindChannelAsync(1, 10))!.DeletedAt);

        await sut.SyncAllAsync(1, [(10, "v", GuildChannelKind.Voice, 0)]); // back
        var back = await db.FindChannelAsync(1, 10);
        Assert.Null(back!.DeletedAt);
        Assert.Equal(TrackedChannelMode.Reward, back.Mode);
    }

    [Fact]
    public async Task Remove_SoftDeletesConfigured()
    {
        using var db = NewDb();
        var sut = new ChannelSyncService(db);

        await sut.UpsertAsync(1, 10, "v", GuildChannelKind.Voice, 0);
        var c = await db.FindChannelAsync(1, 10);
        c!.Mode = TrackedChannelMode.Reward;
        await db.SaveChangesAsync();

        await sut.RemoveAsync(1, 10);

        var row = await db.FindChannelAsync(1, 10);
        Assert.NotNull(row);
        Assert.NotNull(row!.DeletedAt);
    }

    [Fact]
    public async Task ListByKind_FiltersAndOrders()
    {
        using var db = NewDb();
        var sut = new ChannelSyncService(db);

        await sut.UpsertAsync(1, 10, "zeta", GuildChannelKind.Voice, position: 5);
        await sut.UpsertAsync(1, 11, "alpha", GuildChannelKind.Voice, position: 1);
        await sut.UpsertAsync(1, 12, "text", GuildChannelKind.Text, position: 0);

        var voice = await db.ListChannelsByKindAsync(1, GuildChannelKind.Voice);

        Assert.Equal(new ulong[] { 11, 10 }, voice.Select(c => c.ChannelId).ToArray());
    }
}
