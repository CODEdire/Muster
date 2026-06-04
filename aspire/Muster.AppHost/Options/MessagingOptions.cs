using Azure.Provisioning.ServiceBus;

namespace Muster.AppHost.Options;

public record MessagingOptions
{
    public bool UseExisting { get; init; } = false;

    public bool IsZoneRedundant { get; init; } = true;

    public bool DisableLocalAuth { get; init; } = true;

    public ServiceBusPublicNetworkAccess PublicNetworkAccess { get; init; } = ServiceBusPublicNetworkAccess.Enabled;
}