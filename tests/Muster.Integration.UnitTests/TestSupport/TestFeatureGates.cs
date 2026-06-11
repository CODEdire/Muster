using Muster.Contracts;
using Muster.Infrastructure.Services.Platform;

namespace Muster.IntegrationTests.TestSupport;

/// <summary>Feature-gate doubles for tests that aren't exercising the gate itself.</summary>
internal static class TestFeatureGates
{
    /// <summary>An <see cref="IFeatureGate"/> that reports every feature fully <see cref="FeatureAvailability.Enabled"/>
    /// — lets quest/shop command tests run as if the platform, plan, and guild all permit the feature.</summary>
    public static readonly IFeatureGate AlwaysOn = new AlwaysOnGate();

    private sealed class AlwaysOnGate : IFeatureGate
    {
        public Task<FeatureVerdict> EvaluateAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default)
            => Task.FromResult(new FeatureVerdict(FeatureAvailability.Enabled, FeatureGateReason.Enabled));

        public Task<bool> IsEnabledAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
