using Microsoft.EntityFrameworkCore;
using Muster.Domain;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Membership;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Tracking;
using Muster.Persistence;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// Role tiers: EconomyManager / TrackingManager / QuestManager / Auditor, each a dedicated role list.
/// Auditor is implied by any management role. Admin bypasses every check.
/// </summary>
public class SplitRoleTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task Seed(MusterDbContext db, ulong roleId, ulong userId)
    {
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        await new RoleSyncService(db).UpsertAsync(1, roleId, $"R{roleId}", permissions: 0);
        await new MemberSyncService(db).UpsertAsync(1, userId, "u", null, null, roleIds: [roleId]);
    }

    [Fact]
    public async Task EconomyManagerRole_GrantsMintViaCurrencyAuthorizer()
    {
        using var db = NewDb();
        await Seed(db, roleId: 700, userId: 10);

        var config = new ConfigCommandService(db, Microsoft.Extensions.Options.Options.Create(new CurrencyRetentionOptions()));
        await config.ToggleEconomyManagerRoleAsync(1, 700);

        var auth = new CurrencyAuthorizer(new GuildAuthorizationService(db));
        Assert.True(await auth.AuthorizeAsync(new GuildActor(1, 10), subjectUserId: 99, CurrencyPermission.Mint));
        Assert.True(await auth.AuthorizeAsync(new GuildActor(1, 10), subjectUserId: 99, CurrencyPermission.Adjust));
    }

    [Fact]
    public async Task TrackingManagerRole_DoesNotGrantMint()
    {
        using var db = NewDb();
        await Seed(db, roleId: 800, userId: 11);

        var config = new ConfigCommandService(db, Microsoft.Extensions.Options.Options.Create(new CurrencyRetentionOptions()));
        await config.ToggleTrackingManagerRoleAsync(1, 800);

        var auth = new CurrencyAuthorizer(new GuildAuthorizationService(db));
        Assert.False(await auth.AuthorizeAsync(new GuildActor(1, 11), subjectUserId: 99, CurrencyPermission.Mint));
    }

    [Fact]
    public async Task AuditorRole_IsImpliedByMutatingRoles_AndDirectMapping()
    {
        using var db = NewDb();
        await Seed(db, roleId: 1000, userId: 13);

        var config = new ConfigCommandService(db, Microsoft.Extensions.Options.Options.Create(new CurrencyRetentionOptions()));
        await config.ToggleAuditorRoleAsync(1, 1000);

        var roles = new GuildAuthorizationService(db);
        Assert.True(await roles.IsAuditorAsync(1, 13));

        // EconomyManager implicitly grants auditor (any management role does).
        await new RoleSyncService(db).UpsertAsync(1, 1100, "Econ", permissions: 0);
        await new MemberSyncService(db).UpsertAsync(1, 14, "v", null, null, roleIds: [1100]);
        await config.ToggleEconomyManagerRoleAsync(1, 1100);
        Assert.True(await roles.IsAuditorAsync(1, 14));
    }

    [Fact]
    public async Task PlainMember_IsNoneOfTheSplitRoles()
    {
        using var db = NewDb();
        await Seed(db, roleId: 500, userId: 20);

        var roles = new GuildAuthorizationService(db);
        Assert.False(await roles.IsEconomyManagerAsync(1, 20));
        Assert.False(await roles.IsTrackingManagerAsync(1, 20));
        Assert.False(await roles.IsAuditorAsync(1, 20));
    }

    [Fact]
    public async Task Owner_BypassesEverySplitRoleCheck()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 99);

        var roles = new GuildAuthorizationService(db);
        Assert.True(await roles.IsEconomyManagerAsync(1, 99));
        Assert.True(await roles.IsTrackingManagerAsync(1, 99));
        Assert.True(await roles.IsAuditorAsync(1, 99));
    }

    [Fact]
    public async Task TrackingManagerRole_GrantsTrackingMutationsViaAuthorizer()
    {
        using var db = NewDb();
        await Seed(db, roleId: 1500, userId: 30);

        var config = new ConfigCommandService(db, Microsoft.Extensions.Options.Options.Create(new CurrencyRetentionOptions()));
        await config.ToggleTrackingManagerRoleAsync(1, 1500);

        var roles = new GuildAuthorizationService(db);
        var auth = new TrackingAuthorizer(roles);
        Assert.True(await auth.AuthorizeAsync(new GuildActor(1, 30), 0, TrackingPermission.ManageSessions));
        Assert.True(await auth.AuthorizeAsync(new GuildActor(1, 30), 0, TrackingPermission.ManageChannels));
        Assert.True(await auth.AuthorizeAsync(new GuildActor(1, 30), 0, TrackingPermission.ManageMultipliers));
        // SetPrivacy on another member also requires staff; tracking manager qualifies.
        Assert.True(await auth.AuthorizeAsync(new GuildActor(1, 30), subjectUserId: 999, TrackingPermission.SetPrivacy));
    }

    [Fact]
    public async Task EconomyManagerRole_DoesNotGrantTrackingMutations()
    {
        using var db = NewDb();
        await Seed(db, roleId: 1600, userId: 31);

        var config = new ConfigCommandService(db, Microsoft.Extensions.Options.Options.Create(new CurrencyRetentionOptions()));
        await config.ToggleEconomyManagerRoleAsync(1, 1600);

        var auth = new TrackingAuthorizer(new GuildAuthorizationService(db));
        Assert.False(await auth.AuthorizeAsync(new GuildActor(1, 31), 0, TrackingPermission.ManageSessions));
        Assert.False(await auth.AuthorizeAsync(new GuildActor(1, 31), 0, TrackingPermission.ManageChannels));
    }

    [Fact]
    public async Task TrackingManager_ImpliesAuditor()
    {
        using var db = NewDb();
        await Seed(db, roleId: 1800, userId: 33);

        var config = new ConfigCommandService(db, Microsoft.Extensions.Options.Options.Create(new CurrencyRetentionOptions()));
        await config.ToggleTrackingManagerRoleAsync(1, 1800);

        var roles = new GuildAuthorizationService(db);
        Assert.True(await roles.IsAuditorAsync(1, 33));
    }

    [Fact]
    public async Task SelfSetPrivacy_AllowedWithoutAnyStaffRole()
    {
        using var db = NewDb();
        await Seed(db, roleId: 1900, userId: 40);

        var auth = new TrackingAuthorizer(new GuildAuthorizationService(db));
        // Self always allowed regardless of staff flag.
        Assert.True(await auth.AuthorizeAsync(new GuildActor(1, 40), subjectUserId: 40, TrackingPermission.SetPrivacy));
        // Setting someone else's privacy without staff role is denied.
        Assert.False(await auth.AuthorizeAsync(new GuildActor(1, 40), subjectUserId: 41, TrackingPermission.SetPrivacy));
    }

    [Fact]
    public async Task ViewMemberStats_SelfAlways_OthersRequireTrackingStaff()
    {
        using var db = NewDb();
        await Seed(db, roleId: 2000, userId: 50);

        var auth = new TrackingAuthorizer(new GuildAuthorizationService(db));
        // Non-staff can view own stats.
        Assert.True(await auth.AuthorizeAsync(new GuildActor(1, 50), subjectUserId: 50, TrackingPermission.ViewMemberStats));
        // Non-staff cannot view another member's stats.
        Assert.False(await auth.AuthorizeAsync(new GuildActor(1, 50), subjectUserId: 51, TrackingPermission.ViewMemberStats));

        // Grant tracking-manager role → may now view anyone's.
        await new ConfigCommandService(db, Microsoft.Extensions.Options.Options.Create(new CurrencyRetentionOptions()))
            .ToggleTrackingManagerRoleAsync(1, 2000);
        Assert.True(await auth.AuthorizeAsync(new GuildActor(1, 50), subjectUserId: 51, TrackingPermission.ViewMemberStats));
    }

    [Fact]
    public async Task Toggle_EachNewRole_RoundTrips()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var sut = new ConfigCommandService(db, Microsoft.Extensions.Options.Options.Create(new CurrencyRetentionOptions()));

        await RoundTrip(sut.ToggleEconomyManagerRoleAsync,  GuildRoleTier.EconomyManager);
        await RoundTrip(sut.ToggleTrackingManagerRoleAsync, GuildRoleTier.TrackingManager);
        await RoundTrip(sut.ToggleAuditorRoleAsync,         GuildRoleTier.Auditor);

        async Task RoundTrip(Func<ulong, ulong, CancellationToken, Task<CommandResult>> toggle, GuildRoleTier tier)
        {
            var added = await toggle(1, 1234, default);
            Assert.Contains("Added", added.Message);
            Assert.True(await db.GuildRoleMappings.AsNoTracking().AnyAsync(m => m.RoleId == 1234 && m.Tiers == tier));

            var removed = await toggle(1, 1234, default);
            Assert.Contains("Removed", removed.Message);
            Assert.False(await db.GuildRoleMappings.AsNoTracking().AnyAsync(m => m.RoleId == 1234)); // row deleted
        }
    }
}
