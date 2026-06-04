using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning.SignalR;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;

namespace Muster.AppHost.PlatformExtensions;

internal static class SignalRConstants
{
    /// <summary>Aspire resource id for the Azure SignalR service. Consumers reference it by this name via
    /// <c>WithMusterSignalR</c>; Aspire publishes the connection string as <c>ConnectionStrings:signalr</c>.</summary>
    public const string ResourceName = "signalr";

    public const string ResourceNameParam = "signalRName";
    public const string ResourceGroupNameParam = "signalRResourceGroupName";
}

/// <summary>
/// AppHost composition for Azure SignalR Service — the backplane that offloads the Blazor Server interactive
/// circuit (a SignalR hub) so the web can scale out past a single replica. Provisioned in <b>Default</b> service
/// mode (the app server keeps connections to Azure SignalR, which fans out to clients).
///
/// <para><b>Run mode</b>: no-op. Local dev keeps the circuit in-process (single replica), so there's nothing to
/// offload and no Azure dependency — matches the existing UAT/local behavior.</para>
///
/// <para><b>Publish mode</b>: provisioned per env (or bound to an existing service via <c>UseExisting</c>).
/// <c>WithMusterSignalR</c> on the web grants the <i>SignalR App Server</i> role + publishes the connection
/// string the <c>Microsoft.Azure.SignalR</c> SDK reads.</para>
/// </summary>
internal static class SignalRExtensions
{
    /// <summary>Platform step: adds the Azure SignalR service (no-op in run mode → stays null so the web keeps its
    /// in-process circuit locally).</summary>
    public static MusterPlatformBuilder AddSignalR(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        if (builder.ExecutionContext.IsRunMode)
        {
            return p;
        }

        var config = builder.Configuration.GetSection(nameof(SignalROptions)).Get<SignalROptions>();

        // Default service mode (the AddAzureSignalR default) = app-server connections, which is what Blazor Server's
        // hub offload needs (Serverless mode is for client-only / REST scenarios).
        var resource = builder.AddAzureSignalR(SignalRConstants.ResourceName);

        if (builder.ExecutionContext.IsPublishMode && config?.UseExisting == true)
        {
            resource.PublishAsExisting(
                builder.AddParameter(SignalRConstants.ResourceNameParam),
                builder.AddParameter(SignalRConstants.ResourceGroupNameParam)
            );
        }

        resource.ConfigureInfrastructure(infrastructure =>
        {
            var signalr = infrastructure.GetProvisionableResources()
                .OfType<SignalRService>()
                .Single();

            if (!signalr.IsExistingResource && config is not null)
            {
                signalr.Sku = new SignalRResourceSku { Name = config.SkuName, Capacity = config.Capacity };
                signalr.PublicNetworkAccess = config.PublicNetworkAccess;
            }
        });

        p.SignalR = resource;
        return p;
    }

    /// <summary>References Azure SignalR on a consumer and grants the <i>SignalR App Server</i> role (so the app
    /// server can negotiate + route client connections). Publishes <c>ConnectionStrings:signalr</c>.</summary>
    public static IResourceBuilder<T> WithMusterSignalR<T>(
        this IResourceBuilder<T> consumer, IResourceBuilder<AzureSignalRResource> signalR)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        consumer
            .WithReference(signalR)
            .WithRoleAssignments(signalR, SignalRBuiltInRole.SignalRAppServer);

        return consumer;
    }
}
