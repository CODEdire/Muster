using Azure.Provisioning.Storage;

namespace Muster.AppHost.Options;

public record StorageOptions
{
    public bool UseExisting { get; init; } = false;

    public StorageSkuName Sku { get; init; } = StorageSkuName.StandardLrs;

    public bool AllowBlobPublicAccess { get; init; } = false;

    public bool AllowSharedKeyAccess { get; init; } = false;

    public bool EnableHttpsTrafficOnly { get; init; } = true;

    public StorageMinimumTlsVersion MinimumTlsVersion { get; init; } = StorageMinimumTlsVersion.Tls1_2;

    public StoragePublicNetworkAccess PublicNetworkAccess { get; init; } = StoragePublicNetworkAccess.Enabled;
}
