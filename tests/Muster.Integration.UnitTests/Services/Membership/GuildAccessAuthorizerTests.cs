using Microsoft.EntityFrameworkCore;
using Muster.Domain;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands.Membership;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Persistence;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// Web admin pages declare a <see cref="GuildAccessTier"/> bitset; the dispatcher OR-checks every set flag
/// against <see cref="GuildAuthorizationService"/>. These tests pin: admin always passes, admin-only locks
/// everyone else out, single-tier requirements gate correctly, combined tiers permit any holder.
/// </summary>
public class GuildAccessAuthorizerTests
{
    private const ulong GuildId = 1;
    private const ulong Owner = 1;
    private const ulong RandomMember = 50;

    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task SeedRole(MusterDbContext db, ulong roleId, ulong userId)
    {
        await new GuildProvisioningService(db).EnsureGuildAsync(GuildId, "G", null, ownerId: Owner);
        await new RoleSyncService(db).UpsertAsync(GuildId, roleId, $"R{roleId}", permissions: 0);
        await new MemberSyncService(db).UpsertAsync(GuildId, userId, "u", null, null, roleIds: [roleId]);
    }

    private static ConfigCommandService Config(MusterDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(new CurrencyRetentionOptions()));

    [Fact]
    public async Task Admin_AlwaysPasses_RegardlessOfRequiredTier()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(GuildId, "G", null, ownerId: Owner);
        var roles = new GuildAuthorizationService(db);

        // Owner is admin by the lockout-proof bypass.
        Assert.True(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, Owner, GuildAccessTier.Admin));
        Assert.True(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, Owner, GuildAccessTier.Auditor));
        Assert.True(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, Owner, GuildAccessTier.EconomyManager));
        Assert.True(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, Owner, GuildAccessTier.AnyStaff));
    }

    [Fact]
    public async Task AdminOnlyTier_DeniesEveryNonAdmin()
    {
        using var db = NewDb();
        await SeedRole(db, roleId: 700, userId: 10);
        var config = Config(db);
        // Give member every non-admin role.
        await config.ToggleEconomyManagerRoleAsync(GuildId, 700);
        await config.ToggleTrackingManagerRoleAsync(GuildId, 700);
        await config.ToggleQuestManagerRoleAsync(GuildId, 700);
        await config.ToggleAuditorRoleAsync(GuildId, 700);

        var roles = new GuildAuthorizationService(db);
        Assert.False(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, 10, GuildAccessTier.Admin));
    }

    [Fact]
    public async Task SingleTier_GatesByThatRoleOnly()
    {
        using var db = NewDb();
        await SeedRole(db, roleId: 800, userId: 11);
        await Config(db).ToggleEconomyManagerRoleAsync(GuildId, 800);

        var roles = new GuildAuthorizationService(db);
        Assert.True(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, 11, GuildAccessTier.EconomyManager));
        Assert.False(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, 11, GuildAccessTier.TrackingManager));
        Assert.False(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, 11, GuildAccessTier.QuestManager));
        // EconomyManager implies Auditor (mgmt-role inclusion in IsAuditorAsync) — passes Auditor too.
        Assert.True(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, 11, GuildAccessTier.Auditor));
    }

    [Fact]
    public async Task CombinedTier_AnyMatchPasses()
    {
        using var db = NewDb();
        await SeedRole(db, roleId: 900, userId: 12);
        await Config(db).ToggleTrackingManagerRoleAsync(GuildId, 900);

        var roles = new GuildAuthorizationService(db);
        // Page accepts Economy OR Tracking; user is Tracking → passes.
        var either = GuildAccessTier.EconomyManager | GuildAccessTier.TrackingManager;
        Assert.True(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, 12, either));
    }

    [Fact]
    public async Task AnyStaff_AcceptsEveryManagementTier()
    {
        using var db = NewDb();
        await SeedRole(db, roleId: 1000, userId: 13);
        await Config(db).ToggleQuestManagerRoleAsync(GuildId, 1000);

        var roles = new GuildAuthorizationService(db);
        Assert.True(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, 13, GuildAccessTier.AnyStaff));
    }

    [Fact]
    public async Task PlainMember_DeniedByEveryRequirement()
    {
        using var db = NewDb();
        await SeedRole(db, roleId: 1100, userId: RandomMember);
        // No role toggles — viewer holds role 1100 but it's mapped to nothing.

        var roles = new GuildAuthorizationService(db);
        Assert.False(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, RandomMember, GuildAccessTier.Admin));
        Assert.False(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, RandomMember, GuildAccessTier.AnyStaff));
        Assert.False(await GuildAccessAuthorizer.IsAuthorizedAsync(roles, GuildId, RandomMember,
            GuildAccessTier.EconomyManager | GuildAccessTier.Auditor));
    }
}
