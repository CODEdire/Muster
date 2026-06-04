namespace Muster.AppHost.Options;

/// <summary>Container App sizing + autoscale for the web. Bound from <c>WebContainerOptions</c> (appsettings /
/// user-secrets / App Configuration). Scale-out is safe because the Blazor circuit is offloaded to Azure SignalR;
/// <see cref="MaxReplicas"/> &gt; 1 additionally enables ingress session affinity + an HTTP scale rule.</summary>
public record WebContainerOptions
{
    public double Cpu { get; init; } = 0.5;

    public string Memory { get; init; } = "1.0Gi";

    public int MinReplicas { get; init; } = 1;

    // Default 1 (single replica). Raise via config (local options or production) to scale out — that auto-enables
    // ingress session affinity + the HTTP scale rule, which scale-out needs alongside the Azure SignalR backplane.
    public int MaxReplicas { get; init; } = 1;

    /// <summary>HTTP autoscale trigger: scale out when a replica exceeds this many concurrent requests. Only
    /// applied when <see cref="MaxReplicas"/> &gt; 1. 0 = no HTTP rule (stays at <see cref="MinReplicas"/>).</summary>
    public int HttpScaleConcurrentRequests { get; init; } = 100;

    public int TerminationGracePeriodSeconds { get; init; } = 30;
}
