using Muster.Contracts;
using Muster.Infrastructure.Services.Platform;
using Xunit;

namespace Muster.IntegrationTests.Services.Platform;

public class FeatureGateTests
{
    private sealed class Platform(bool available) : IPlatformFeatureSource
    {
        public Task<bool> IsAvailableAsync(PlatformFeature feature, CancellationToken ct = default) => Task.FromResult(available);
    }

    private sealed class Billing(bool entitled) : IFeatureEntitlementSource
    {
        public Task<bool> IsEntitledAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default) => Task.FromResult(entitled);
    }

    private sealed class Guild(bool enabled) : IGuildFeatureSource
    {
        public Task<bool> IsEnabledAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default) => Task.FromResult(enabled);
    }

    private static Task<FeatureVerdict> Evaluate(bool platform, bool billing, bool guild)
        => new FeatureGate(new Platform(platform), new Billing(billing), new Guild(guild))
            .EvaluateAsync(1, PlatformFeature.Shop);

    [Fact]
    public async Task PlatformOff_IsUnavailable_AndCannotEnable()
    {
        var v = await Evaluate(platform: false, billing: true, guild: true);
        Assert.Equal(FeatureAvailability.Unavailable, v.Availability);
        Assert.Equal(FeatureGateReason.PlatformDisabled, v.Reason);
        Assert.False(v.CanEnable);
    }

    [Fact]
    public async Task NotEntitled_IsUnavailable_NotEntitledReason()
    {
        var v = await Evaluate(platform: true, billing: false, guild: true);
        Assert.Equal(FeatureAvailability.Unavailable, v.Availability);
        Assert.Equal(FeatureGateReason.NotEntitled, v.Reason);
    }

    [Fact]
    public async Task GuildOff_IsAvailable_AndCanEnable()
    {
        var v = await Evaluate(platform: true, billing: true, guild: false);
        Assert.Equal(FeatureAvailability.Available, v.Availability);
        Assert.Equal(FeatureGateReason.GuildDisabled, v.Reason);
        Assert.True(v.CanEnable);
        Assert.False(v.IsEnabled);
    }

    [Fact]
    public async Task AllOn_IsEnabled()
    {
        var v = await Evaluate(platform: true, billing: true, guild: true);
        Assert.Equal(FeatureAvailability.Enabled, v.Availability);
        Assert.True(v.IsEnabled);
    }

    [Fact]
    public async Task Platform_TakesPrecedence_OverBillingAndGuild()
    {
        // Most-restrictive-wins: platform off masks everything below it.
        var v = await Evaluate(platform: false, billing: false, guild: false);
        Assert.Equal(FeatureGateReason.PlatformDisabled, v.Reason);
    }
}
