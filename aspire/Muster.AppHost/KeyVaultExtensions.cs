using Aspire.Hosting.Azure;

namespace Muster.AppHost;

internal static class KeyVaultConstants
{
    /// <summary>Aspire resource id for the Key Vault. Consumers reference it by this name via
    /// <c>WithReference(kv)</c>; Aspire publishes the vault URI as <c>ConnectionStrings:kv</c>.</summary>
    public const string ResourceName = "kv";

    /// <summary>Name of the RSA key the Data Protection key-ring is wrapped with.</summary>
    public const string DataProtectionWrapKeyName = "muster-dp-wrap";
}

/// <summary>
/// AppHost composition for Azure Key Vault — the home for all <i>secrets</i> (Discord bot token, OAuth
/// client secret, Azure SignalR connection string, anything else sensitive). Values that change but
/// aren't sensitive go in <see cref="AppConfigurationExtensions"/> instead.
///
/// <para><b>Run mode</b>: no-op. Local dev keeps using <c>dotnet user-secrets</c> +
/// <c>appsettings.Development.json</c> — there's no first-party KV emulator in Aspire 13.3.5.</para>
///
/// <para><b>Publish mode</b>: Aspire provisions a new Key Vault per environment, RBAC-enabled.
/// Web/Bot workload identities get <c>Key Vault Secrets User</c> + <c>Key Vault Crypto User</c>
/// automatically when the projects are wired with <c>WithReference(kv)</c>. The
/// <see cref="KeyVaultConstants.DataProtectionWrapKeyName"/> RSA key is provisioned alongside the
/// vault for wrapping the Data Protection key-ring; see <c>docs/deployment.md</c>
/// "Data Protection setup".</para>
/// </summary>
internal static class KeyVaultExtensions
{
    /// <summary>Adds Muster's Key Vault. Returns <c>null</c> in run mode so callers can skip wiring
    /// references on consumers — local dev keeps user-secrets / appsettings.Development.json.</summary>
    public static IResourceBuilder<AzureKeyVaultResource>? AddMusterKeyVault(this IDistributedApplicationBuilder builder)
    {
        if (builder.ExecutionContext.IsRunMode)
        {
            return null;
        }

        return builder.AddAzureKeyVault(KeyVaultConstants.ResourceName);
    }
}
