using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;

namespace Muster.AppHost;

/// <summary>Resource id for Muster's Container Apps Environment.</summary>
internal static class ContainerRegistryConstants
{
    /// <summary>Aspire resource id for the Container Apps Environment that runs web + bot + migrations.</summary>
    public const string EnvironmentResourceName = "muster-env";

    // NOTE: AcrNameParameter / AcrResourceGroupParameter intentionally removed. ACR is no longer modeled
    // as an Aspire resource (see type doc on ContainerRegistryExtensions) because Aspire 13.3.5 emits
    // cross-RG role assignments inline (BCP139). azd is steered to the shared ACR via the standard
    // AZURE_CONTAINER_REGISTRY_ENDPOINT + AZURE_CONTAINER_REGISTRY_NAME env vars instead.
}

/// <summary>
/// AppHost composition for Muster's Container Apps Environment.
///
/// <para><b>Run mode</b>: not used. Aspire orchestrates projects + emulators locally without a CA
/// Environment.</para>
///
/// <para><b>Publish mode</b>: creates a Muster-dedicated Container Apps Environment per env
/// (dev / staging / prod). Per <c>docs/deployment.md</c>, the CA Environment is per-env (different
/// VNet/telemetry posture) but the ACR is shared infra.</para>
///
/// <para><b>Why ACR is NOT bound here</b>: Aspire 13.3.5's <c>WithAzureContainerRegistry(acr)</c>
/// emits the <c>AcrPull</c> role assignment INLINE in the env module, which trips BCP139 when the
/// ACR lives in a different resource group than the deployment (our "shared platform RG" pattern).
/// The role assignment needs to be a sub-module scoped to the ACR's RG; the inline form requires
/// same-scope resources. Pre-Aspire-13.4 the workaround is to skip the binding entirely + grant
/// AcrPull on each Container App's user-assigned MI manually after deploy. See
/// <c>docs/deployment.md</c> "ACR cross-RG workaround".</para>
///
/// <para>azd is told which registry to push to via <c>AZURE_CONTAINER_REGISTRY_ENDPOINT</c> +
/// <c>AZURE_CONTAINER_REGISTRY_NAME</c> set on the azd env (see deploy runbook). Without those,
/// azd would auto-provision a new ACR in the deployment RG — defeating the shared-ACR plan.</para>
/// </summary>
internal static class ContainerRegistryExtensions
{
    /// <summary>Adds the Muster-dedicated Container Apps Environment (publish-mode only — null in run
    /// mode). ACR binding is handled out-of-band via azd env vars + post-deploy AcrPull grants; see
    /// the type doc above.</summary>
    public static IResourceBuilder<AzureContainerAppEnvironmentResource>? AddMusterContainerAppEnvironment(
        this IDistributedApplicationBuilder builder)
    {
        if (builder.ExecutionContext.IsRunMode)
        {
            return null;
        }

        return builder.AddAzureContainerAppEnvironment(ContainerRegistryConstants.EnvironmentResourceName);
    }
}
