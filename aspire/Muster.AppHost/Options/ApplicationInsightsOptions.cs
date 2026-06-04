namespace Muster.AppHost.Options;

public record ApplicationInsightsOptions
{
    public bool UseExisting { get; init; } = false;
}
