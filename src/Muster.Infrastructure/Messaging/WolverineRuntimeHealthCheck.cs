using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wolverine.Runtime;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Readiness probe for the local Wolverine runtime. Wolverine 6 ships no first-party
/// <c>AddWolverine()</c> health-check extension, so we inspect <see cref="IWolverineRuntime"/> directly:
/// a host can resolve the singleton but the runtime isn't started → not ready to accept work yet
/// (handler graph not warmed, transports not listening). Transport-level outages (ASB unreachable,
/// SQL durable store down) are surfaced by Wolverine's own resilience pipeline + the App Insights
/// dependency tab — this check intentionally stays cheap (no I/O) so it's safe on a fast probe.
///
/// <para>Tagged <c>ready</c> in <see cref="InfrastructureExtensions"/> wiring so it gates the
/// <c>/health</c> readiness endpoint but never trips the <c>/alive</c> liveness probe (a missed
/// startup pass shouldn't restart the container in a tight loop).</para>
/// </summary>
public sealed class WolverineRuntimeHealthCheck(IWolverineRuntime runtime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Wolverine 6 exposes its readiness state via AssertHasStarted() (throws when bootstrap is still
        // mid-flight or partial — e.g. listeners not yet attached). No public boolean equivalent.
        try
        {
            runtime.AssertHasStarted();
            return Task.FromResult(HealthCheckResult.Healthy("Wolverine runtime started."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Wolverine runtime not started.", ex));
        }
    }
}
