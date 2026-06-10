using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.Configuration;
using Muster.AppHost.Core;
using Muster.AppHost.Options;

namespace Muster.AppHost.PlatformExtensions;

internal static class StorageConstants
{
    /// <summary>Aspire resource id for the Storage account.</summary>
    public const string AccountResourceName = "storage";

    /// <summary>Aspire resource id AND the actual blob container name that holds the ASP.NET Data Protection
    /// key ring. Consumers reference it by this name via <c>WithReference(dpKeys)</c>; Aspire publishes the
    /// container URI as <c>ConnectionStrings:dpkeys</c>.</summary>
    public const string DataProtectionContainerName = "dpkeys";

    /// <summary>Aspire resource id AND blob container name for shop listing/storefront images. Web references it
    /// via <c>WithReference</c>; Aspire publishes the container URI as <c>ConnectionStrings:shopimages</c>.</summary>
    public const string ShopImagesContainerName = "shopimages";

    public const string ResourceNameParam = "storageAccountName";
    public const string ResourceGroupNameParam = "storageAccountResourceGroupName";
}

/// <summary>
/// AppHost composition for Azure Storage. Today only the <c>dpkeys</c> blob container exists (backs the
/// ASP.NET Data Protection key ring, paired with the Key Vault RSA wrap key
/// <see cref="KeyVaultConstants.DataProtectionWrapKeyName"/>). The account itself is general-purpose
/// — additional containers (uploads, exports, message attachments, …) can be added as siblings here when
/// the need lands. Web/Bot get an account-scope <c>Storage Blob Data Contributor</c> role assignment
/// (granted explicitly in <see cref="WithMusterDataProtection"/>), which covers every container on the account.
///
/// <para><b>Run mode</b>: Azurite emulator container with a persistent volume so local DP keys (and any
/// future container data) survive <c>dotnet run</c> restarts.</para>
///
/// <para><b>Publish mode</b>: Aspire provisions a new Storage account per environment (or binds an existing
/// one via <c>UseExisting</c>), RBAC-enabled and hardened per <see cref="StorageOptions"/>.</para>
/// </summary>
internal static class StorageExtensions
{
    /// <summary>Platform step: adds the Storage account (Azurite locally) + the <c>dpkeys</c> Data Protection
    /// container, stashing both on the platform.</summary>
    public static MusterPlatformBuilder AddStorage(this MusterPlatformBuilder p)
    {
        var builder = p.Inner;
        var config = builder.Configuration.GetSection(nameof(StorageOptions)).Get<StorageOptions>();
        var resource = builder.AddAzureStorage(StorageConstants.AccountResourceName);

        if (builder.ExecutionContext.IsRunMode)
        {
            // Local Azurite with a persistent volume — local data survives AppHost restarts.
            resource.RunAsEmulator(emulator => emulator
                .WithDataVolume()
                .WithLifetime(ContainerLifetime.Persistent));
        }
        else
        {
            if (builder.ExecutionContext.IsPublishMode && config?.UseExisting == true)
            {
                resource.PublishAsExisting(
                    builder.AddParameter(StorageConstants.ResourceNameParam),
                    builder.AddParameter(StorageConstants.ResourceGroupNameParam)
                );
            }

            resource.ConfigureInfrastructure(infrastructure =>
            {
                var account = infrastructure.GetProvisionableResources()
                    .OfType<StorageAccount>()
                    .Single();

                if (!account.IsExistingResource && config is not null)
                {
                    account.Sku = new StorageSku { Name = config.Sku };
                    account.AllowBlobPublicAccess = config.AllowBlobPublicAccess;
                    account.AllowSharedKeyAccess = config.AllowSharedKeyAccess;
                    account.EnableHttpsTrafficOnly = config.EnableHttpsTrafficOnly;
                    account.MinimumTlsVersion = config.MinimumTlsVersion;
                    account.PublicNetworkAccess = config.PublicNetworkAccess;
                }
            });
        }

        p.Storage = resource;
        p.DataProtectionKeys = resource.WithDataProtectionKeys();
        p.ShopImages = resource.AddBlobContainer(StorageConstants.ShopImagesContainerName);
        return p;
    }

    /// <summary>Adds the <c>dpkeys</c> blob container that holds the Data Protection key ring. Returns the
    /// container builder for <c>WithMusterDataProtection(dpKeys)</c> wiring on web + bot.</summary>
    public static IResourceBuilder<AzureBlobStorageContainerResource> WithDataProtectionKeys(this IResourceBuilder<AzureStorageResource> storage)
        => storage.AddBlobContainer(StorageConstants.DataProtectionContainerName);

    /// <summary>Wires Data Protection on a consumer: references the <c>dpkeys</c> container (publishes the container
    /// URI), grants <i>Storage Blob Data Contributor</i> on the account explicitly (read+write the key ring — not
    /// relying on Aspire's implicit default), and sets the two env vars <c>Infrastructure.AddMusterConnectorProtection</c>
    /// reads to pick Azure DP (Blob key ring + KV wrap) over the local SQL fallback.</summary>
    public static IResourceBuilder<T> WithMusterDataProtection<T>(
        this IResourceBuilder<T> consumer,
        IResourceBuilder<AzureStorageResource> storage,
        IResourceBuilder<AzureBlobStorageContainerResource> dpKeys)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        consumer
            .WithReference(dpKeys)
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageBlobDataContributor)
            .WithEnvironment("DataProtection__Container", StorageConstants.DataProtectionContainerName)
            .WithEnvironment("DataProtection__WrapKeyName", KeyVaultConstants.DataProtectionWrapKeyName);

        return consumer;
    }
}
