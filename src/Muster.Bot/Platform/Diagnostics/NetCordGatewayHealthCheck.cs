using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetCord.Gateway;

namespace Muster.Bot.Platform.Diagnostics;

/// <summary>
/// Readiness probe for the Discord gateway session. The bot is functionally dead while disconnected —
/// no slash commands, no button presses, no voice-state updates land. <see cref="GatewayClient.Status"/>
/// is the official surface: <see cref="WebSocketStatus.Ready"/> after the gateway handshake completes,
/// flips to <see cref="WebSocketStatus.Connecting"/> during reconnect attempts and
/// <see cref="WebSocketStatus.Disconnected"/> when no session exists.
///
/// <para>Returns <c>Healthy</c> only on Ready. Connecting is treated as Degraded so a fresh start /
/// transient reconnect doesn't immediately go red — App Insights will still see the dip via the
/// gateway-event flow, but ACA (if probes are ever wired for the bot) won't restart on a 3-second blip.
/// Disconnected is Unhealthy.</para>
///
/// <para>Bot today exposes no HTTP probe endpoints (it's a Worker, not a WebApplication), so this check
/// is currently inspection-only — useful for in-process diagnostics + ready to plug into a future
/// minimal probe listener. Documented in <c>docs/observability.md</c> "Health checks: bot".</para>
/// </summary>
public sealed class NetCordGatewayHealthCheck(GatewayClient client) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(client.Status switch
        {
            WebSocketStatus.Ready => HealthCheckResult.Healthy("Gateway session active."),
            WebSocketStatus.Connecting => HealthCheckResult.Degraded("Gateway session reconnecting."),
            _ => HealthCheckResult.Unhealthy($"Gateway session disconnected ({client.Status})."),
        });
    }
}
