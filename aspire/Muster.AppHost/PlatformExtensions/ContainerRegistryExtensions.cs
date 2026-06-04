using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.ContainerRegistry;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;

namespace Muster.AppHost.PlatformExtensions;

/// <summary>Resource id for Muster's Container Apps Environment.</summary>
internal static class ContainerConstants
{
    /// <summary>Aspire resource id for the Container Apps Environment that runs web + bot + migrations.</summary>
    public const string EnvironmentResourceName = "muster-env";
    public const string RegistryResourceName = "muster-acr";

    public const string AppEnvironmentResourceNameParam = "appConfigName";
    public const string AppEnvironmeResourceGroupNameParam = "appConfigResourceGroupName";

    public const string ContainerRegistryResourceNameParam = "containerRegistryName";
    public const string ContainerRegistryResourceGroupNameParam = "containerRegistryResourceGroupName";
}

internal static class ContainerExtensions
{
    public static MusterPlatformBuilder AddContainerRegistry(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        if (builder.ExecutionContext.IsRunMode)
            return p;

        var config = builder.Configuration.GetSection(nameof(ContainerRegistryOptions)).Get<ContainerRegistryOptions>();
        var resource = builder.AddAzureContainerRegistry(ContainerConstants.RegistryResourceName);            

        if (builder.ExecutionContext.IsPublishMode && config?.UseExisting == true)
        {
            resource = resource.PublishAsExisting(
                builder.AddParameter(ContainerConstants.ContainerRegistryResourceNameParam),
                builder.AddParameter(ContainerConstants.ContainerRegistryResourceGroupNameParam)
            );
        }
        else
        {
            resource = resource.WithPurgeTask("0 1 * * *", ago: TimeSpan.FromDays(7), keep: 5);
        }

        resource.ConfigureInfrastructure(infrastructure =>
        {
            var acr = infrastructure.GetProvisionableResources()
                .OfType<ContainerRegistryService>()
                .Single();

            if (!acr.IsExistingResource && config is not null)
            {
                acr.IsAnonymousPullEnabled = config.IsAnonymousPullEnabled;
                acr.NetworkRuleBypassOptions = config.NetworkRuleBypassOption;
                acr.PublicNetworkAccess = config.PublicNetworkAccess;

                //Soft Delete

                acr.Sku = new ContainerRegistrySku()
                {
                    Name = ContainerRegistrySkuName.Basic
                };
            }
        });


        p.ContainerRegistry = resource;
        return p;
    }

    /// <summary>Platform step: adds the Container Apps Environment, attaching the registry + Log Analytics workspace
    /// already stashed on the platform. Call after <see cref="AddContainerRegistry"/> and the Log Analytics step.</summary>
    public static MusterPlatformBuilder AddContainerEnvironment(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        if (builder.ExecutionContext.IsRunMode)
            return p;

        var config = builder.Configuration.GetSection(nameof(AppContainerEnvironmentOptions)).Get<AppContainerEnvironmentOptions>();
        var acrConfig = builder.Configuration.GetSection(nameof(ContainerRegistryOptions)).Get<ContainerRegistryOptions>();

        // A shared registry bound AsExisting lives in another resource group, so it's referenced here as a
        // cross-RG `existing` resource — and a roleAssignment can't be scoped to a cross-RG resource from this
        // module (BCP139). When that's the case we strip the inline AcrPull grant below and hand it off to a
        // one-time out-of-band step (docs/deployment.md "Shared ACR pull grant").
        bool acrIsExisting = builder.ExecutionContext.IsPublishMode && acrConfig?.UseExisting == true;

        var resource = builder.AddAzureContainerAppEnvironment(ContainerConstants.EnvironmentResourceName);

        // WithAzureContainerRegistry attaches the registry to the environment: it creates the env's user-assigned
        // managed identity, points every container app's `registries` block at it for image pull, AND inlines an
        // AcrPull role assignment in THIS module. That inline grant is valid for a registry in the deploy RG; for a
        // cross-RG existing registry it's stripped in ConfigureInfrastructure below.
        if (p.ContainerRegistry is not null)
            resource.WithAzureContainerRegistry(p.ContainerRegistry);

        // Send container console + system logs to the shared Log Analytics workspace (same store as App Insights).
        if (p.LogAnalytics is not null)
            resource.WithAzureLogAnalyticsWorkspace(p.LogAnalytics);

        if (builder.ExecutionContext.IsPublishMode && config?.UseExisting == true)
        {
            //In publish mode, we will use the existing resource if it exists
            resource.PublishAsExisting(
                builder.AddParameter(ContainerConstants.AppEnvironmentResourceNameParam),
                builder.AddParameter(ContainerConstants.AppEnvironmeResourceGroupNameParam)
            );
        }
        else
        {
            resource
                .WithHttpsUpgrade()
                .WithDashboard();
        }

        resource.ConfigureInfrastructure(infrastructure =>
        {
            var containerAppEnv = infrastructure.GetProvisionableResources()
                .OfType<ContainerAppManagedEnvironment>()
                .Single();

            if (!containerAppEnv.IsExistingResource && config is not null)
            {
                containerAppEnv.PublicNetworkAccess = config.PublicNetworkAccess;
                containerAppEnv.IsEnabled = config.PeerTrafficEncryptionEnabled;
                //containerAppEnv.IsZoneRedundant = true; //Requires VNET. Future TODO.
            }

            // Cross-RG shared registry: remove the inline AcrPull role assignment WithAzureContainerRegistry added.
            // It targets the registry via a cross-RG `existing` ref, which bicep rejects (BCP139). The env identity
            // still pulls — its AcrPull on the shared registry is granted once, out of band, in the registry's RG
            // (docs/deployment.md "Shared ACR pull grant"). The registries block + env MI it set up are untouched.
            if (acrIsExisting)
            {
                foreach (var roleAssignment in infrastructure.GetProvisionableResources().OfType<RoleAssignment>().ToList())
                    infrastructure.Remove(roleAssignment);
            }
        });

        p.ContainerEnvironment = resource;
        return p;
    }
}
