using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning.KeyVault;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;

namespace Muster.AppHost.PlatformExtensions;

internal static class KeyVaultConstants
{
    /// <summary>Aspire resource id for the Key Vault. Consumers reference it by this name via
    /// <c>WithReference(kv)</c>; Aspire publishes the vault URI as <c>ConnectionStrings:kv</c>.</summary>
    public const string ResourceName = "kv";

    /// <summary>Name of the RSA key the Data Protection key-ring is wrapped with.</summary>
    public const string DataProtectionWrapKeyName = "muster-dp-wrap";

    public const string ResourceNameParam = "keyVaultName";
    public const string ResourceGroupNameParam = "keyVaultResourceGroupName";
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
/// automatically when the projects are wired with <c>WithReference(kv)</c>.</para>
///
/// <para><b>The <see cref="KeyVaultConstants.DataProtectionWrapKeyName"/> RSA key is NOT provisioned here</b> —
/// neither Aspire nor Azure.Provisioning models a Key Vault <i>key</i> resource (only secrets). It's a one-time
/// per-environment bootstrap step: create the key once with <c>az keyvault key create</c> (the workload identity's
/// Crypto User role can wrap/unwrap it but cannot create it). See <c>docs/deployment.md</c>
/// "Data Protection wrap key".</para>
/// </summary>
internal static class KeyVaultExtensions
{
    /// <summary>Platform step: adds the Key Vault (no-op in run mode → stays null so consumers skip wiring it;
    /// local dev keeps user-secrets / appsettings.Development.json).</summary>
    public static MusterPlatformBuilder AddKeyVault(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        if (builder.ExecutionContext.IsRunMode)
        {
            return p;
        }

        var config = builder.Configuration.GetSection(nameof(KeyVaultOptions)).Get<KeyVaultOptions>();
        var resource = builder.AddAzureKeyVault(KeyVaultConstants.ResourceName);

        if (builder.ExecutionContext.IsPublishMode && config?.UseExisting == true)
        {
            resource.PublishAsExisting(
                builder.AddParameter(KeyVaultConstants.ResourceNameParam),
                builder.AddParameter(KeyVaultConstants.ResourceGroupNameParam)
            );
        }

        resource.ConfigureInfrastructure(infrastructure =>
        {
            var kv = infrastructure.GetProvisionableResources()
                .OfType<KeyVaultService>()
                .Single();

            if (!kv.IsExistingResource && config is not null)
            {
                kv.Properties.EnableRbacAuthorization = config.EnableRbacAuthorization;
                kv.Properties.EnableSoftDelete = config.EnableSoftDelete;
                kv.Properties.SoftDeleteRetentionInDays = config.SoftDeleteRetentionInDays;
                kv.Properties.EnablePurgeProtection = config.EnablePurgeProtection;
                kv.Properties.PublicNetworkAccess = config.PublicNetworkAccess;
            }
        });

        p.KeyVault = resource;
        return p;
    }

    /// <summary>References the Key Vault on a consumer and grants the workload identity the roles Muster needs.
    /// <see cref="WithReference"/> publishes <c>ConnectionStrings:kv</c>; the explicit role set replaces Aspire's
    /// Secrets-User-only default with both roles we actually use:
    /// <list type="bullet">
    /// <item><b>Secrets User</b> — read app secrets directly AND resolve App Configuration Key Vault references.</item>
    /// <item><b>Crypto User</b> — wrap/unwrap the Data Protection key ring with the <c>muster-dp-wrap</c> RSA key.</item>
    /// </list></summary>
    public static IResourceBuilder<T> WithMusterKeyVault<T>(
        this IResourceBuilder<T> consumer, IResourceBuilder<AzureKeyVaultResource> keyVault)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        consumer
            .WithReference(keyVault)
            .WithRoleAssignments(keyVault, KeyVaultBuiltInRole.KeyVaultSecretsUser, KeyVaultBuiltInRole.KeyVaultCryptoUser);

        return consumer;
    }
}
