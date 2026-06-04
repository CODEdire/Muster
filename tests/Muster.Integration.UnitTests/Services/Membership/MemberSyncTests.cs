using Microsoft.EntityFrameworkCore;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Membership;
using Muster.IntegrationTests.TestSupport;

namespace Muster.IntegrationTests;

public class MemberSyncTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task Upsert_CreatesUserAndMember_ThenUpdatesInPlace()
    {
        using var db = NewDb();
        var sut = new MemberSyncService(db);

        await sut.UpsertAsync(1, 10, "alice", "Alice", "hash1", nickname: "Al", roleIds: [100, 200]);
        await sut.UpsertAsync(1, 10, "alice2", "Alice Renamed", "hash2", nickname: "Ally", roleIds: [100]);

        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.GuildMembers.CountAsync());

        var user = await db.Users.SingleAsync();
        Assert.Equal("alice2", user.Username);
        Assert.Equal("Alice Renamed", user.GlobalName);

        var member = await db.GuildMembers.SingleAsync();
        Assert.Equal("Ally", member.Nickname);
        Assert.Equal(new ulong[] { 100 }, member.RoleIds);
    }

    [Fact]
    public async Task Upsert_SameUser_AcrossGuilds_SharesUserButSeparateMembers()
    {
        using var db = NewDb();
        var sut = new MemberSyncService(db);

        await sut.UpsertAsync(1, 10, "alice", "Alice", null);
        await sut.UpsertAsync(2, 10, "alice", "Alice", null);

        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(2, await db.GuildMembers.CountAsync());
    }

    [Fact]
    public async Task RemoveMember_DropsMembership_KeepsUser()
    {
        using var db = NewDb();
        var sut = new MemberSyncService(db);
        await sut.UpsertAsync(1, 10, "alice", "Alice", null);

        await sut.RemoveMemberAsync(1, 10);

        Assert.Equal(0, await db.GuildMembers.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync()); // shared user record stays
    }

    [Fact]
    public async Task Authorization_DerivesAdminAndStaff_FromRoleMapping()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null);

        // Map app permissions to Discord role ids (not to users directly).
        await db.MapRoleAsync(1, 900, GuildRoleTier.Admin);
        await db.MapRoleAsync(1, 800, GuildRoleTier.EconomyManager);

        var sync = new MemberSyncService(db);
        await sync.UpsertAsync(1, 10, "admin", null, null, roleIds: [900]);  // has admin role
        await sync.UpsertAsync(1, 20, "econ", null, null, roleIds: [800]);   // has economy-manager role
        await sync.UpsertAsync(1, 30, "member", null, null, roleIds: [123]); // no mapped role

        var auth = new GuildAuthorizationService(db);

        Assert.True(await auth.IsAdminAsync(1, 10));
        Assert.True(await auth.IsAnyStaffAsync(1, 10));  // admins are implicitly staff
        Assert.False(await auth.IsAdminAsync(1, 20));
        Assert.True(await auth.IsAnyStaffAsync(1, 20));  // economy manager is staff
        Assert.False(await auth.IsAdminAsync(1, 30));
        Assert.False(await auth.IsAnyStaffAsync(1, 30));
    }

    [Fact]
    public async Task Authorization_ReflectsRoleChange_AfterResync()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null);
        await db.MapRoleAsync(1, 900, GuildRoleTier.Admin);

        var sync = new MemberSyncService(db);
        var auth = new GuildAuthorizationService(db);

        await sync.UpsertAsync(1, 10, "u", null, null, roleIds: [900]);
        Assert.True(await auth.IsAdminAsync(1, 10));

        // Discord role removed -> member-update re-syncs the snapshot -> admin revoked.
        await sync.UpsertAsync(1, 10, "u", null, null, roleIds: []);
        Assert.False(await auth.IsAdminAsync(1, 10));
    }
}
