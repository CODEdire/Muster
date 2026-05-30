using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;

namespace Muster.AppHost;

/// <summary>Resource ids + user-secret parameter names for the shared image registry + Container Apps
/// Environment that hosts Muster in publish. Centralised so AppHost and any future consumer agree.</summary>
internal static class ContainerRegistryConstants
{
    /// <summary>Aspire resource id for the Azure Container Registry binding.</summary>
    public const string AcrResourceName = "acr";

    /// <summary>Aspire resource id for the Container Apps Environment that runs web + bot + migrations.</summary>
    public const string EnvironmentResourceName = "muster-env";

    /// <summary>User-secret parameter — name of the existing Azure Container Registry resource.</summary>
    public const string AcrNameParameter = "acr-name";

    /// <summary>User-secret parameter — resource group the existing ACR lives in (shared across products).</summary>
    public const string AcrResourceGroupParameter = "acr-resource-group";
}

/// <summary>
/// AppHost composition for the shared image registry + Muster's Container Apps Environment.
///
/// <para><b>Run mode</b>: not used. Aspire orchestrates projects + emulators locally without an ACR or
/// CA Environment.</para>
///
/// <para><b>Publish mode</b>: binds an <i>existing</i> ACR (typically a shared registry sitting in a
/// shared "platform" resource group) via <c>AsExisting</c>, then creates a Muster-dedicated Container
/// Apps Environment that pulls from it. Per <c>docs/deployment.md</c>, we use a dedicated env per
/// environment (dev / staging / prod) — shared ACR for image storage is fine, shared runtime
/// environment is not (different VNet/telemetry posture).</para>
///
/// <para>The ACR's managed identity grant for pulling is handled by Aspire automatically when the
/// Container Apps Environment is bound to it — Aspire emits the <c>AcrPull</c> role assignment in the
/// generated Bicep.</para>
/// </summary>
internal static class ContainerRegistryExtensions
{
    /// <summary>Adds the shared ACR (publish-mode only — returns null in run mode) and the Muster-dedicated
    /// Container Apps Environment bound to it. Idempotent: safe to call from AppHost.cs unconditionally.</summary>
    public static IResourceBuilder<AzureContainerAppEnvironmentResource>? AddMusterContainerAppEnvironment(
        this IDistributedApplicationBuilder builder)
    {
        if (builder.ExecutionContext.IsRunMode)
        {
            return null;
        }

        var acrName = builder.AddParameter(ContainerRegistryConstants.AcrNameParameter);
        var acrResourceGroup = builder.AddParameter(ContainerRegistryConstants.AcrResourceGroupParameter);

        var acr = builder.AddAzureContainerRegistry(ContainerRegistryConstants.AcrResourceName)
            .AsExisting(acrName, acrResourceGroup);

        var environment = builder.AddAzureContainerAppEnvironment(ContainerRegistryConstants.EnvironmentResourceName)
            .WithAzureContainerRegistry(acr);

        return environment;
    }
}
