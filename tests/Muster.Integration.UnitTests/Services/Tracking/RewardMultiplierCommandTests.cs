using Microsoft.EntityFrameworkCore;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Infrastructure.Commands.Tracking;
using Xunit;

namespace Muster.IntegrationTests;

public class RewardMultiplierCommandTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static RewardMultiplierCommandService Sut(MusterDbContext db) =>
        new(db, new AlwaysAllowTrackingAuthorizer());

    private const ulong Actor = 7;

    [Fact]
    public async Task AddOneOff_Validates_AndCreatesEnabled()
    {
        using var db = NewDb();
        var sut = Sut(db);
        var now = DateTimeOffset.UtcNow;

        Assert.True((await sut.AddOneOffAsync(1, Actor, "bad", 2m, MultiplierScope.All, now, now)).IsError);          // end<=start
        Assert.True((await sut.AddOneOffAsync(1, Actor, "zero", 0m, MultiplierScope.All, now, now.AddHours(1))).IsError); // factor<=0
        Assert.True((await sut.AddOneOffAsync(1, Actor, "noscope", 2m, MultiplierScope.None, now, now.AddHours(1))).IsError);

        var ok = await sut.AddOneOffAsync(1, Actor, "Happy hour", 2m, MultiplierScope.All, now, now.AddHours(1));
        Assert.False(ok.IsError);
        var m = await db.RewardMultipliers.SingleAsync();
        Assert.Equal(MultiplierKind.OneOff, m.Kind);
        Assert.True(m.Enabled);
        Assert.Equal(2m, m.Factor);
    }

    [Fact]
    public async Task AddRecurring_RequiresDays()
    {
        using var db = NewDb();
        var sut = Sut(db);
        Assert.True((await sut.AddRecurringAsync(1, Actor, "x", 1.5m, MultiplierScope.All, WeekDays.None, new(19, 0), new(22, 0))).IsError);
        Assert.False((await sut.AddRecurringAsync(1, Actor, "x", 1.5m, MultiplierScope.All, WeekDays.Friday, new(19, 0), new(22, 0))).IsError);
    }

    [Fact]
    public async Task AddRole_RequiresRole()
    {
        using var db = NewDb();
        var sut = Sut(db);
        Assert.True((await sut.AddRoleAsync(1, Actor, "vip", 2m, MultiplierScope.All, roleId: 0)).IsError);
        Assert.False((await sut.AddRoleAsync(1, Actor, "vip", 2m, MultiplierScope.All, roleId: 99)).IsError);
    }

    [Fact]
    public async Task EnableAndRemove_RoundTrip()
    {
        using var db = NewDb();
        var sut = Sut(db);
        await sut.AddRoleAsync(1, Actor, "vip", 2m, MultiplierScope.All, roleId: 99);
        var id = (await db.RewardMultipliers.SingleAsync()).Id;

        Assert.False((await sut.SetEnabledAsync(1, Actor, id, false)).IsError);
        Assert.False((await db.RewardMultipliers.SingleAsync()).Enabled);

        Assert.True((await sut.SetEnabledAsync(1, Actor, Guid.NewGuid(), true)).IsError);  // unknown id
        Assert.True((await sut.RemoveAsync(1, Actor, Guid.NewGuid())).IsError);            // unknown id

        Assert.False((await sut.RemoveAsync(1, Actor, id)).IsError);
        Assert.Equal(0, await db.RewardMultipliers.CountAsync());
    }

    [Fact]
    public async Task NonStaff_Forbidden_OnEveryMutation()
    {
        using var db = NewDb();
        var deny = new RewardMultiplierCommandService(db, new AlwaysDenyTrackingAuthorizer());
        var now = DateTimeOffset.UtcNow;

        Assert.Equal("Forbidden", (await deny.AddOneOffAsync(1, Actor, "x", 2m, MultiplierScope.All, now, now.AddHours(1))).Message);
        Assert.Equal("Forbidden", (await deny.AddRecurringAsync(1, Actor, "x", 1.5m, MultiplierScope.All, WeekDays.Friday, new(19, 0), new(22, 0))).Message);
        Assert.Equal("Forbidden", (await deny.AddRoleAsync(1, Actor, "vip", 2m, MultiplierScope.All, 99)).Message);
        Assert.Equal("Forbidden", (await deny.SetEnabledAsync(1, Actor, Guid.NewGuid(), true)).Message);
        Assert.Equal("Forbidden", (await deny.RemoveAsync(1, Actor, Guid.NewGuid())).Message);
        Assert.Equal(0, await db.RewardMultipliers.CountAsync()); // never even reached the DB
    }
}
