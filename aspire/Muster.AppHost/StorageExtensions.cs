using Aspire.Hosting.Azure;

namespace Muster.AppHost;

internal static class StorageConstants
{
    /// <summary>Aspire resource id for the Storage account.</summary>
    public const string AccountResourceName = "storage";

    /// <summary>Aspire resource id AND the actual blob container name that holds the ASP.NET Data Protection
    /// key ring. Consumers reference it by this name via <c>WithReference(dpKeys)</c>; Aspire publishes the
    /// container URI as <c>ConnectionStrings:dpkeys</c>.</summary>
    public const string DataProtectionContainerName = "dpkeys";
}

/// <summary>
/// AppHost composition for Azure Storage. Today only the <c>dpkeys</c> blob container exists (backs the
/// ASP.NET Data Protection key ring, paired with the Key Vault RSA wrap key
/// <see cref="KeyVaultConstants.DataProtectionWrapKeyName"/>). The account itself is general-purpose
/// — additional containers (uploads, exports, message attachments, …) can be added as siblings here when
/// the need lands. Web/Bot's account-scope <c>Storage Blob Data Contributor</c> role assignment (granted
/// by <c>WithReference</c> on any container in the account) covers them automatically.
///
/// <para><b>Run mode</b>: Azurite emulator container with a persistent volume so local DP keys (and any
/// future container data) survive <c>dotnet run</c> restarts.</para>
///
/// <para><b>Publish mode</b>: Aspire provisions a new Storage account per environment, RBAC-enabled.
/// See <c>docs/deployment.md</c> "Configuration sources".</para>
/// </summary>
internal static class StorageExtensions
{
    /// <summary>Adds the Storage account. Returns the account builder so callers chain
    /// <c>.WithDataProtectionKeys()</c> (and any future <c>.AddBlobContainer(...)</c> for new uses).</summary>
    public static IResourceBuilder<AzureStorageResource> AddMusterStorage(this IDistributedApplicationBuilder builder)
    {
        var storage = builder.AddAzureStorage(StorageConstants.AccountResourceName);

        if (builder.ExecutionContext.IsRunMode)
        {
            // Local Azurite with a persistent volume — local data survives AppHost restarts.
            storage.RunAsEmulator(emulator => emulator
                .WithDataVolume()
                .WithLifetime(ContainerLifetime.Persistent));
        }

        return storage;
    }

    /// <summary>Adds the <c>dpkeys</c> blob container that holds the Data Protection key ring. Returns the
    /// container builder for <c>WithReference(dpKeys)</c> wiring on web + bot.</summary>
    public static IResourceBuilder<AzureBlobStorageContainerResource> WithDataProtectionKeys(this IResourceBuilder<AzureStorageResource> storage)
        => storage.AddBlobContainer(StorageConstants.DataProtectionContainerName);
}
