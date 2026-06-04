using Azure.Provisioning.ContainerRegistry;

namespace Muster.AppHost.Options;

public record ContainerRegistryOptions
{
    public bool UseExisting { get; init; } = false;

    public ContainerRegistrySkuName Sku { get; init; } = ContainerRegistrySkuName.Basic;

    public bool IsAnonymousPullEnabled { get; init; } = false;

    public ContainerRegistryNetworkRuleBypassOption NetworkRuleBypassOption { get; init; } = ContainerRegistryNetworkRuleBypassOption.AzureServices;

    public ContainerRegistryPublicNetworkAccess PublicNetworkAccess { get; init; } = ContainerRegistryPublicNetworkAccess.Enabled;
}
