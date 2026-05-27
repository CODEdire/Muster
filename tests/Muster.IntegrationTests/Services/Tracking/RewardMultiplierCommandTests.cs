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

    private static RewardMultiplierCommandService Sut(MusterDbContext db) => new(db);

    [Fact]
    public async Task AddOneOff_Validates_AndCreatesEnabled()
    {
        using var db = NewDb();
        var sut = Sut(db);
        var now = DateTimeOffset.UtcNow;

        Assert.True((await sut.AddOneOffAsync(1, "bad", 2m, MultiplierScope.All, now, now)).IsError);          // end<=start
        Assert.True((await sut.AddOneOffAsync(1, "zero", 0m, MultiplierScope.All, now, now.AddHours(1))).IsError); // factor<=0
        Assert.True((await sut.AddOneOffAsync(1, "noscope", 2m, MultiplierScope.None, now, now.AddHours(1))).IsError);

        var ok = await sut.AddOneOffAsync(1, "Happy hour", 2m, MultiplierScope.All, now, now.AddHours(1));
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
        Assert.True((await sut.AddRecurringAsync(1, "x", 1.5m, MultiplierScope.All, WeekDays.None, new(19, 0), new(22, 0))).IsError);
        Assert.False((await sut.AddRecurringAsync(1, "x", 1.5m, MultiplierScope.All, WeekDays.Friday, new(19, 0), new(22, 0))).IsError);
    }

    [Fact]
    public async Task AddRole_RequiresRole()
    {
        using var db = NewDb();
        var sut = Sut(db);
        Assert.True((await sut.AddRoleAsync(1, "vip", 2m, MultiplierScope.All, roleId: 0)).IsError);
        Assert.False((await sut.AddRoleAsync(1, "vip", 2m, MultiplierScope.All, roleId: 99)).IsError);
    }

    [Fact]
    public async Task EnableAndRemove_RoundTrip()
    {
        using var db = NewDb();
        var sut = Sut(db);
        await sut.AddRoleAsync(1, "vip", 2m, MultiplierScope.All, roleId: 99);
        var id = (await db.RewardMultipliers.SingleAsync()).Id;

        Assert.False((await sut.SetEnabledAsync(1, id, false)).IsError);
        Assert.False((await db.RewardMultipliers.SingleAsync()).Enabled);

        Assert.True((await sut.SetEnabledAsync(1, Guid.NewGuid(), true)).IsError);  // unknown id
        Assert.True((await sut.RemoveAsync(1, Guid.NewGuid())).IsError);            // unknown id

        Assert.False((await sut.RemoveAsync(1, id)).IsError);
        Assert.Equal(0, await db.RewardMultipliers.CountAsync());
    }
}
