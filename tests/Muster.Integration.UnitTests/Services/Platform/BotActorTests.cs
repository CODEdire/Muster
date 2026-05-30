using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// Bots are synced (so they can serve as an API key's service actor) but hidden from human-facing pickers, and a
/// key may only be bound to act as the creating admin's own account or a bot member — never an arbitrary member.
/// </summary>
public class BotActorTests
{
    private const ulong Guild = 1, Owner = 1, Human = 50, Bot = 99, Stranger = 777;

    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>().UseInMemoryDatabase($"muster-{Guid.NewGuid()}").Options);

    private static async Task<MusterDbContext> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(Guild, "G", null, ownerId: Owner);
        db.Users.Add(new DiscordUser { Id = Human, Username = "Human", IsBot = false });
        db.GuildMembers.Add(new GuildMember { GuildId = Guild, UserId = Human, JoinedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new DiscordUser { Id = Bot, Username = "Bot", IsBot = true });
        db.GuildMembers.Add(new GuildMember { GuildId = Guild, UserId = Bot, JoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return db;
    }

    // ---- Sync stores IsBot --------------------------------------------------

    [Fact]
    public async Task SyncGuild_StoresIsBot()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(Guild, "G", null, ownerId: Owner);
        await new MemberSyncService(db).SyncGuildAsync(Guild,
        [
            new MemberSnapshot(Human, "Human", null, null, null, [], IsBot: false),
            new MemberSnapshot(Bot, "Bot", null, null, null, [], IsBot: true),
        ]);

        Assert.True((await db.Users.SingleAsync(u => u.Id == Bot)).IsBot);
        Assert.False((await db.Users.SingleAsync(u => u.Id == Human)).IsBot);
    }

    // ---- Human-facing picker hides bots ------------------------------------

    [Fact]
    public async Task GetMembers_ExcludesBots()
    {
        var db = await SeededAsync();
        var members = await new WebAdminService(db).GetMembersAsync(Guild);

        Assert.Contains(members, m => m.UserId == Human);
        Assert.DoesNotContain(members, m => m.UserId == Bot);
    }

    // ---- API-key actor binding ---------------------------------------------

    [Fact]
    public async Task Bind_ToSelf_Allowed()
    {
        var db = await SeededAsync();
        var created = await new ApiClientService(db).CreateAsync(Guild, "k", ["write:quests"], actsAsUserId: Owner, createdByUserId: Owner);
        Assert.Equal(Owner, created.Client.ActsAsUserId);
    }

    [Fact]
    public async Task Bind_ToGuildBot_Allowed()
    {
        var db = await SeededAsync();
        var created = await new ApiClientService(db).CreateAsync(Guild, "k", ["write:quests"], actsAsUserId: Bot, createdByUserId: Owner);
        Assert.Equal(Bot, created.Client.ActsAsUserId);
    }

    [Fact]
    public async Task Bind_Unbound_Allowed()
    {
        var db = await SeededAsync();
        var created = await new ApiClientService(db).CreateAsync(Guild, "k", ["read:quests"], actsAsUserId: 0, createdByUserId: Owner);
        Assert.Equal(0u, created.Client.ActsAsUserId);
    }

    [Fact]
    public async Task Bind_ToArbitraryMember_Rejected()
    {
        var db = await SeededAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ApiClientService(db).CreateAsync(Guild, "k", ["write:quests"], actsAsUserId: Human, createdByUserId: Owner));
    }

    [Fact]
    public async Task Bind_ToNonMember_Rejected()
    {
        var db = await SeededAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ApiClientService(db).CreateAsync(Guild, "k", ["write:quests"], actsAsUserId: Stranger, createdByUserId: Owner));
    }
}
