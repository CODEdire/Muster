using Muster.Domain.Entities.Tracking;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Tracking;
using Xunit;

namespace Muster.UnitTests;

public class RewardMultiplierTests
{
    private static readonly DateTimeOffset Wed7pm = new(2026, 5, 27, 19, 0, 0, TimeSpan.Zero); // a Wednesday, UTC

    private static MultiplierSet Set(MultiplierStacking stacking = MultiplierStacking.HighestWins, decimal cap = 0m, params RewardMultiplier[] m)
        => new(m, stacking, cap, TimeZoneInfo.Utc);

    [Fact]
    public void Empty_ReturnsOne()
        => Assert.Equal(1m, Set().Factor(MultiplierScope.Sessions, Wed7pm));

    [Fact]
    public void OneOff_ActiveOnlyWithinWindow()
    {
        var m = new RewardMultiplier
        {
            Kind = MultiplierKind.OneOff, Factor = 2m, Scope = MultiplierScope.All,
            StartsAt = Wed7pm.AddHours(-1), EndsAt = Wed7pm.AddHours(1),
        };
        var set = Set(m: m);

        Assert.Equal(2m, set.Factor(MultiplierScope.Sessions, Wed7pm));
        Assert.Equal(1m, set.Factor(MultiplierScope.Sessions, Wed7pm.AddHours(2))); // after window
    }

    [Fact]
    public void Scope_FiltersByPlane()
    {
        var m = new RewardMultiplier
        {
            Kind = MultiplierKind.OneOff, Factor = 3m, Scope = MultiplierScope.Messages,
            StartsAt = Wed7pm.AddHours(-1), EndsAt = Wed7pm.AddHours(1),
        };
        var set = Set(m: m);

        Assert.Equal(3m, set.Factor(MultiplierScope.Messages, Wed7pm));
        Assert.Equal(1m, set.Factor(MultiplierScope.BackgroundVoice, Wed7pm)); // different plane
    }

    [Fact]
    public void Recurring_WeeknightWindow()
    {
        var m = new RewardMultiplier
        {
            Kind = MultiplierKind.Recurring, Factor = 1.5m, Scope = MultiplierScope.All,
            Days = WeekDays.Wednesday, StartTime = new TimeOnly(19, 0), EndTime = new TimeOnly(22, 0),
        };
        var set = Set(m: m);

        Assert.Equal(1.5m, set.Factor(MultiplierScope.BackgroundVoice, Wed7pm));
        Assert.Equal(1m, set.Factor(MultiplierScope.BackgroundVoice, Wed7pm.AddHours(4))); // 11pm, past window
        Assert.Equal(1m, set.Factor(MultiplierScope.BackgroundVoice, Wed7pm.AddDays(1))); // Thursday
    }

    [Fact]
    public void Recurring_WrapsPastMidnight()
    {
        var m = new RewardMultiplier
        {
            Kind = MultiplierKind.Recurring, Factor = 2m, Scope = MultiplierScope.All,
            Days = WeekDays.Wednesday, StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(2, 0),
        };
        var set = Set(m: m);

        Assert.Equal(2m, set.Factor(MultiplierScope.Sessions, new DateTimeOffset(2026, 5, 27, 23, 0, 0, TimeSpan.Zero))); // Wed 11pm
        Assert.Equal(2m, set.Factor(MultiplierScope.Sessions, new DateTimeOffset(2026, 5, 28, 1, 0, 0, TimeSpan.Zero)));  // Thu 1am (wrap)
        Assert.Equal(1m, set.Factor(MultiplierScope.Sessions, new DateTimeOffset(2026, 5, 28, 3, 0, 0, TimeSpan.Zero)));  // Thu 3am
    }

    [Fact]
    public void Stacking_HighestWins_VsMultiplicative()
    {
        RewardMultiplier W(decimal f) => new()
        {
            Kind = MultiplierKind.OneOff, Factor = f, Scope = MultiplierScope.All,
            StartsAt = Wed7pm.AddHours(-1), EndsAt = Wed7pm.AddHours(1),
        };

        Assert.Equal(2m, Set(MultiplierStacking.HighestWins, 0m, W(2m), W(1.5m)).Factor(MultiplierScope.Sessions, Wed7pm));
        Assert.Equal(3m, Set(MultiplierStacking.Multiplicative, 0m, W(2m), W(1.5m)).Factor(MultiplierScope.Sessions, Wed7pm));
    }

    [Fact]
    public void Cap_ClampsEffectiveFactor()
    {
        RewardMultiplier W(decimal f) => new()
        {
            Kind = MultiplierKind.OneOff, Factor = f, Scope = MultiplierScope.All,
            StartsAt = Wed7pm.AddHours(-1), EndsAt = Wed7pm.AddHours(1),
        };

        Assert.Equal(2.5m, Set(MultiplierStacking.Multiplicative, 2.5m, W(2m), W(2m)).Factor(MultiplierScope.Sessions, Wed7pm));
    }

    [Fact]
    public void Role_MultipliesTimeFactor_HighestRoleWins()
    {
        var window = new RewardMultiplier
        {
            Kind = MultiplierKind.OneOff, Factor = 2m, Scope = MultiplierScope.All,
            StartsAt = Wed7pm.AddHours(-1), EndsAt = Wed7pm.AddHours(1),
        };
        var vip = new RewardMultiplier { Kind = MultiplierKind.Role, Factor = 1.5m, Scope = MultiplierScope.All, RoleId = 100 };
        var booster = new RewardMultiplier { Kind = MultiplierKind.Role, Factor = 1.2m, Scope = MultiplierScope.All, RoleId = 200 };
        var set = Set(m: new[] { window, vip, booster });

        // Member with both roles: 2 (window) * 1.5 (highest role) = 3.
        Assert.Equal(3m, set.Factor(MultiplierScope.Sessions, Wed7pm, new ulong[] { 100, 200 }));
        // No roles passed: time factor only.
        Assert.Equal(2m, set.Factor(MultiplierScope.Sessions, Wed7pm));
    }

    [Fact]
    public void DisabledOrZeroFactor_Ignored()
    {
        // The service only loads enabled rows, but a zero/negative factor must never zero out rewards.
        var bad = new RewardMultiplier
        {
            Kind = MultiplierKind.OneOff, Factor = 0m, Scope = MultiplierScope.All,
            StartsAt = Wed7pm.AddHours(-1), EndsAt = Wed7pm.AddHours(1),
        };
        Assert.Equal(1m, Set(m: bad).Factor(MultiplierScope.Sessions, Wed7pm));
    }
}
