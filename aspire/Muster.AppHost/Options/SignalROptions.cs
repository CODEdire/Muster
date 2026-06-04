namespace Muster.AppHost.Options;

public record SignalROptions
{
    public bool UseExisting { get; init; } = false;

    // Standard tier supports the Default service mode used for Blazor Server hub offload + scale-out.
    public string SkuName { get; init; } = "Standard_S1";

    public int Capacity { get; init; } = 1;

    // Azure SignalR's PublicNetworkAccess is a plain string ("Enabled"/"Disabled").
    public string PublicNetworkAccess { get; init; } = "Enabled";
}
