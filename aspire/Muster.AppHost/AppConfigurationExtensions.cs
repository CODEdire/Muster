using Aspire.Hosting.Azure;

namespace Muster.AppHost;

internal static class AppConfigurationConstants
{
    /// <summary>Aspire resource id for the App Configuration store. Consumers reference it by this name via
    /// <c>WithReference(ac)</c>; Aspire publishes the endpoint as <c>ConnectionStrings:appconfig</c>.</summary>
    public const string ResourceName = "appconfig";
}

/// <summary>
/// AppHost composition for Azure App Configuration — the home for all <i>non-secret</i> dynamic config
/// (feature flags, per-environment knobs, role ids, channel ids, anything that isn't sensitive but needs
/// to change without a redeploy). Secrets live in <see cref="KeyVaultExtensions"/>.
///
/// <para><b>Run mode</b>: no-op. Local dev keeps using <c>appsettings.Development.json</c> +
/// <c>dotnet user-secrets</c> — there's no first-party App Configuration emulator in Aspire 13.3.5.</para>
///
/// <para><b>Publish mode</b>: Aspire provisions a new App Configuration store per environment, RBAC-enabled.
/// Web/Bot workload identities get <c>App Configuration Data Reader</c> automatically when projects are
/// wired with <c>WithReference(ac)</c>. See <c>docs/deployment.md</c> "Configuration sources".</para>
/// </summary>
internal static class AppConfigurationExtensions
{
    /// <summary>Adds Muster's App Configuration store. Returns <c>null</c> in run mode so callers can skip
    /// wiring references on consumers — local dev keeps user-secrets / appsettings.Development.json.</summary>
    public static IResourceBuilder<AzureAppConfigurationResource>? AddMusterAppConfiguration(this IDistributedApplicationBuilder builder)
    {
        if (builder.ExecutionContext.IsRunMode)
        {
            return null;
        }

        return builder.AddAzureAppConfiguration(AppConfigurationConstants.ResourceName);
    }
}
