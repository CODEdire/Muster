using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Xunit;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Commands.Membership;

namespace Muster.IntegrationTests;

public class AdminBypassTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task Owner_IsAlwaysAdmin_EvenWithNoRolesOrMapping()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 10);
        // Owner has no GuildMember row and no mapped roles at all.

        var auth = new GuildAuthorizationService(db);
        Assert.True(await auth.IsAdminAsync(1, 10));
        Assert.True(await auth.IsOfficerAsync(1, 10));
        Assert.False(await auth.IsAdminAsync(1, 999));
    }

    [Fact]
    public async Task DiscordAdministratorPermission_GrantsAdminBypass()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);

        // Role 500 carries the Discord Administrator bit but is NOT in the bot's AdminRoleIds mapping.
        await new RoleSyncService(db).UpsertAsync(1, 500, "Admins", DiscordPermissions.Administrator);
        await new MemberSyncService(db).UpsertAsync(1, 10, "u", null, null, roleIds: [500]);

        var auth = new GuildAuthorizationService(db);
        Assert.True(await auth.IsAdminAsync(1, 10));
    }

    [Fact]
    public async Task PlainMember_WithoutMappingOrPermission_IsNotAdmin()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        await new RoleSyncService(db).UpsertAsync(1, 500, "Members", permissions: 0);
        await new MemberSyncService(db).UpsertAsync(1, 10, "u", null, null, roleIds: [500]);

        var auth = new GuildAuthorizationService(db);
        Assert.False(await auth.IsAdminAsync(1, 10));
        Assert.False(await auth.IsOfficerAsync(1, 10));
    }

    [Fact]
    public async Task Config_ToggleAdminRole_AddsThenRemoves()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var sut = new ConfigCommandService(db, Microsoft.Extensions.Options.Options.Create(new Muster.Infrastructure.Services.Currencies.CurrencyRetentionOptions()));

        var added = await sut.ToggleAdminRoleAsync(1, 700);
        Assert.Contains("Added", added.Message);
        Assert.Contains(700ul, (await db.Guilds.SingleAsync()).Settings.AdminRoleIds);

        var removed = await sut.ToggleAdminRoleAsync(1, 700);
        Assert.Contains("Removed", removed.Message);
        Assert.DoesNotContain(700ul, (await db.Guilds.SingleAsync()).Settings.AdminRoleIds);
    }
}
