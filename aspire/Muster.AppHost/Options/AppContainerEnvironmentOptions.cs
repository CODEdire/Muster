using Azure.Provisioning.AppContainers;

namespace Muster.AppHost.Options;

public record AppContainerEnvironmentOptions
{
    public bool UseExisting { get; init; } = false;

    public bool PeerTrafficEncryptionEnabled { get; init; } = true;

    public ContainerAppPublicNetworkAccess PublicNetworkAccess { get; init; } = ContainerAppPublicNetworkAccess.Enabled;
}
