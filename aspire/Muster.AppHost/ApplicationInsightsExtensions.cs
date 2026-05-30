using Aspire.Hosting.Azure;

namespace Muster.AppHost;

internal static class ApplicationInsightsConstants
{
    /// <summary>Aspire resource id for the App Insights component. Consumers reference it by this name via
    /// <c>WithReference(appInsights)</c>; Aspire publishes the connection string as
    /// <c>ConnectionStrings:appinsights</c> AND as the well-known env var
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> that <c>UseAzureMonitor()</c> reads by default.</summary>
    public const string ResourceName = "appinsights";
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
    /// <summary>Adds Muster's App Insights component. Returns <c>null</c> in run mode so callers can skip
    /// wiring references on consumers — locals stay on the Aspire Dashboard via OTLP.</summary>
    public static IResourceBuilder<AzureApplicationInsightsResource>? AddMusterApplicationInsights(this IDistributedApplicationBuilder builder)
    {
        if (builder.ExecutionContext.IsRunMode)
        {
            return null;
        }

        return builder.AddAzureApplicationInsights(ApplicationInsightsConstants.ResourceName);
    }
}
