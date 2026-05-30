using Aspire.Hosting.Azure;
using Aspire.Hosting.DevTunnels;

namespace Muster.AppHost;

/// <summary>
/// AppHost resources that exist only for local development — Aspire dashboard sidecars + the dev tunnel
/// that gives Discord a public URL for the local web. Every method here is a no-op in publish mode, so
/// it's safe to call unconditionally from <c>AppHost.cs</c>.
/// </summary>
internal static class DevExtensions
{
    /// <summary>Aspire resource id for the dev tunnel.</summary>
    private const string DevTunnelResourceName = "web-tunnel";

    /// <summary>Aspire resource id for the Service Bus emulator UI sidecar.</summary>
    private const string EmulatorUiResourceName = "asb-ui";

    /// <summary>
    /// Run-mode only. Creates a Microsoft Dev Tunnel that exposes the local web's HTTPS endpoint as a
    /// public URL Discord's servers can reach — needed for quest-board embed image fetches + title
    /// deep links. Anonymous so Discord doesn't need to auth into the tunnel. First run prompts a
    /// one-time <c>devtunnel user login</c>.
    /// </summary>
    /// <returns>The tunnel resource in run mode; <c>null</c> in publish (where the web has its own
    /// public Container App HTTPS URL and no tunnel is needed).</returns>
    public static IResourceBuilder<DevTunnelResource>? AddMusterDevTunnel(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> web)
    {
        if (builder.ExecutionContext.IsPublishMode)
        {
            return null;
        }

        return builder.AddDevTunnel(DevTunnelResourceName)
            .WithReference(web.GetEndpoint("https"), allowAnonymous: true);
    }

    /// <summary>
    /// Run-mode only. Adds the AJP emulator UI sidecar (peek topics/queues, send + receive test
    /// messages, view message counts) so the local Service Bus emulator is browsable from the Aspire
    /// dashboard. No-op in publish — the live namespace doesn't need a local UI.
    /// </summary>
    public static void AddMusterServiceBusEmulatorUi(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<AzureServiceBusResource> messaging)
    {
        if (builder.ExecutionContext.IsPublishMode)
        {
            return;
        }

        builder.AddAsbEmulatorUi(EmulatorUiResourceName, messaging);
    }
}
