using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;

namespace Muster.AppHost.PlatformExtensions;

internal static class ApplicationInsightsConstants
{
    /// <summary>Aspire resource id for the App Insights component. Consumers reference it by this name via
    /// <c>WithReference(appInsights)</c>; Aspire publishes the connection string as
    /// <c>ConnectionStrings:appinsights</c> AND as the well-known env var
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> that <c>UseAzureMonitor()</c> reads by default.</summary>
    public const string ResourceName = "appinsights";

    public const string ResourceNameParam = "appInsightsName";
    public const string ResourceGroupNameParam = "appInsightsResourceGroupName";
}

/// <summary>
/// AppHost composition for Azure Application Insights — the publish-mode telemetry sink for OTEL traces,
/// metrics, and logs. Each environment gets its own App Insights component (workspace-based) provisioned
/// fresh by Aspire alongside an associated Log Analytics workspace.
///
/// <para><b>Run mode</b>: no-op. Local dev uses the Aspire Dashboard (OTLP exporter wired in
/// <c>ServiceDefaults.ConfigureOpenTelemetry</c>) — App Insights would just add cost and complexity.</para>
///
/// <para><b>Publish mode</b>: provisioned per env. <c>WithReference</c> on web/bot/migrations injects the
/// connection string env var; <c>Azure.Monitor.OpenTelemetry.AspNetCore.UseAzureMonitor()</c> in
/// ServiceDefaults picks it up and routes the existing OTEL pipeline to Azure Monitor. The Aspire
/// Dashboard does NOT deploy to Azure — Container Apps + App Insights + Log Analytics is the prod stack.</para>
/// </summary>
internal static class ApplicationInsightsExtensions
{
    /// <summary>Platform step: adds the App Insights component, backed by the Log Analytics workspace already
    /// stashed on the platform (call after the Log Analytics step). No-op in run mode → stays null so consumers
    /// skip it and locals stay on the Aspire Dashboard via OTLP.</summary>
    public static MusterPlatformBuilder AddApplicationInsights(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        if (builder.ExecutionContext.IsRunMode)
        {
            return p;
        }

        var config = builder.Configuration.GetSection(nameof(ApplicationInsightsOptions)).Get<ApplicationInsightsOptions>();

        // Workspace-based App Insights: bind to the shared Log Analytics workspace when present; otherwise Aspire
        // provisions a dedicated workspace for the component.
        var resource = p.LogAnalytics is not null
            ? builder.AddAzureApplicationInsights(ApplicationInsightsConstants.ResourceName, p.LogAnalytics)
            : builder.AddAzureApplicationInsights(ApplicationInsightsConstants.ResourceName);

        if (builder.ExecutionContext.IsPublishMode && config?.UseExisting == true)
        {
            resource.PublishAsExisting(
                builder.AddParameter(ApplicationInsightsConstants.ResourceNameParam),
                builder.AddParameter(ApplicationInsightsConstants.ResourceGroupNameParam)
            );
        }

        p.ApplicationInsights = resource;
        return p;
    }

    /// <summary>References App Insights on a consumer — publishes <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>,
    /// which <c>UseAzureMonitor()</c> in ServiceDefaults reads to route OTEL to Azure Monitor. No role assignment
    /// needed (the connection string carries an ingestion key).</summary>
    public static IResourceBuilder<T> WithMusterApplicationInsights<T>(
        this IResourceBuilder<T> consumer, IResourceBuilder<AzureApplicationInsightsResource> appInsights)
        where T : IResourceWithEnvironment
    {
        consumer.WithReference(appInsights);
        return consumer;
    }
}
