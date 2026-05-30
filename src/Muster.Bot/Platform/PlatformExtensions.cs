using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Muster.Bot.Platform.Diagnostics;

namespace Muster.Bot.Platform;

/// <summary>
/// Per-feature DI composition for cross-cutting bot plumbing that isn't tied to a single business
/// feature — currently audit retention pruning and the timezone autocomplete provider used by any
/// command that takes a date/time parameter.
/// </summary>
public static class PlatformExtensions
{
    public static IHostApplicationBuilder AddPlatformFeature(this IHostApplicationBuilder builder)
    {
        // Audit prune: drops audit rows beyond Audit:RetentionDays (default 90; 0 disables).
        // Bot host runs it daily (single-instance, no cluster coordination needed for now).
        builder.Services.AddHostedService<AuditPruneScheduler>();

        // Cross-feature autocomplete: timezone codes for any slash command that asks for one.
        builder.Services.AddTransient<TimezoneAutocompleteProvider>();

        // Bot-specific readiness check: gateway session must be Ready. See NetCordGatewayHealthCheck
        // for the reconnect-tolerance + status mapping. Tagged "ready" (NOT "live") so a transient
        // reconnect can never cause a container restart loop.
        builder.Services.AddHealthChecks()
            .AddCheck<NetCordGatewayHealthCheck>(
                name: "discord-gateway",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"]);

        return builder;
    }
}
