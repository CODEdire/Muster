using Aspire.Hosting.Azure;
using Azure.Provisioning.AppConfiguration;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;

namespace Muster.AppHost.PlatformExtensions;

internal static class AppConfigurationConstants
{
    public const string ReferenceName = "appconfig";

    public const string ResourceNameParam = "appConfigName";
    public const string ResourceGroupNameParam = "appConfigResourceGroupName";
}

internal static class AppConfigurationExtensions
{
    public static MusterPlatformBuilder AddAppConfiguration(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        var config = builder.Configuration.GetSection(nameof(AppConfigurationOptions)).Get<AppConfigurationOptions>();
        config?.Validate();

        var resource = builder
            .AddAzureAppConfiguration(AppConfigurationConstants.ReferenceName)
            .RunAsEmulator(emulator =>                                          // In local mode, run as an emulator.
            {
                emulator.WithLifetime(ContainerLifetime.Persistent);
                emulator.WithDataVolume();
            });

        if (builder.ExecutionContext.IsPublishMode && config?.UseExisting == true)
        {
            resource = resource.PublishAsExisting(
                builder.AddParameter(AppConfigurationConstants.ResourceNameParam),
                builder.AddParameter(AppConfigurationConstants.ResourceGroupNameParam)
            );
        }

        resource = resource.ConfigureInfrastructure(infrastructure =>
        {
            var appConfigStore = infrastructure.GetProvisionableResources()
                .OfType<AppConfigurationStore>()
                .Single();

            if (!appConfigStore.IsExistingResource && config is not null)
            {
                appConfigStore.SkuName = config.SkuName;
                appConfigStore.DisableLocalAuth = config.DisableLocalAuth;
                appConfigStore.PublicNetworkAccess = config.PublicNetworkAccess;

                // Standard-only — set only when provided (null = leave Azure's default, valid on Free).
                if (config.EnablePurgeProtection is { } purge) { appConfigStore.EnablePurgeProtection = purge; }
                if (config.SoftDeleteRetentionInDays is { } retention) { appConfigStore.SoftDeleteRetentionInDays = retention; }

                //KeyVault?
            }
        });

        p.AppConfiguration = resource;
        return p;
    }

    public static IResourceBuilder<TProject> WithMusterAppConfiguration<TProject>(
        this IResourceBuilder<TProject> consumer, IResourceBuilder<AzureAppConfigurationResource> appConfig)
        where TProject : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        consumer
            .WithReference(appConfig)
            .WithRoleAssignments(appConfig, AppConfigurationBuiltInRole.AppConfigurationDataReader)
            .WaitFor(appConfig);

        return consumer;
    }
}
