using Aspire.Hosting.Azure;
using Azure.Provisioning.OperationalInsights;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;

namespace Muster.AppHost.PlatformExtensions;

internal static class LogAnalyticsConstants
{
    /// <summary>Aspire resource id for the Log Analytics workspace that backs Application Insights and the
    /// Container Apps Environment (console + system logs).</summary>
    public const string ResourceName = "logs";

    public const string ResourceNameParam = "logAnalyticsName";
    public const string ResourceGroupNameParam = "logAnalyticsResourceGroupName";
}

/// <summary>
/// AppHost composition for the Log Analytics workspace — the single telemetry store shared by Application
/// Insights (workspace-based) and the Container Apps Environment (container console/system logs). Provisioning
/// one workspace and passing it to both keeps all environment telemetry in one queryable place.
///
/// <para><b>Run mode</b>: no-op. Local dev uses the Aspire Dashboard; no Azure workspace is created.</para>
///
/// <para><b>Publish mode</b>: provisioned per env (or bound to an existing one via <c>UseExisting</c>).</para>
/// </summary>
internal static class LogAnalyticsExtensions
{
    /// <summary>Platform step: adds the Log Analytics workspace (no-op in run mode → stays null so App Insights +
    /// the Container App Environment skip it locally).</summary>
    public static MusterPlatformBuilder AddLogAnalytics(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        if (builder.ExecutionContext.IsRunMode)
        {
            return p;
        }

        var config = builder.Configuration.GetSection(nameof(LogAnalyticsOptions)).Get<LogAnalyticsOptions>();
        var resource = builder.AddAzureLogAnalyticsWorkspace(LogAnalyticsConstants.ResourceName);

        if (builder.ExecutionContext.IsPublishMode && config?.UseExisting == true)
        {
            resource.PublishAsExisting(
                builder.AddParameter(LogAnalyticsConstants.ResourceNameParam),
                builder.AddParameter(LogAnalyticsConstants.ResourceGroupNameParam)
            );
        }

        resource.ConfigureInfrastructure(infrastructure =>
        {
            var law = infrastructure.GetProvisionableResources()
                .OfType<OperationalInsightsWorkspace>()
                .Single();

            if (!law.IsExistingResource && config is not null)
            {
                law.Sku = new OperationalInsightsWorkspaceSku { Name = config.Sku };
                law.RetentionInDays = config.RetentionInDays;
                law.PublicNetworkAccessForIngestion = config.PublicNetworkAccessForIngestion;
                law.PublicNetworkAccessForQuery = config.PublicNetworkAccessForQuery;
            }
        });

        p.LogAnalytics = resource;
        return p;
    }
}
