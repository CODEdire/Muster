using Azure.Provisioning.OperationalInsights;

namespace Muster.AppHost.Options;

public record LogAnalyticsOptions
{
    public bool UseExisting { get; init; } = false;

    public OperationalInsightsWorkspaceSkuName Sku { get; init; } = OperationalInsightsWorkspaceSkuName.PerGB2018;

    public int RetentionInDays { get; init; } = 30;

    public OperationalInsightsPublicNetworkAccessType PublicNetworkAccessForIngestion { get; init; } = OperationalInsightsPublicNetworkAccessType.Enabled;

    public OperationalInsightsPublicNetworkAccessType PublicNetworkAccessForQuery { get; init; } = OperationalInsightsPublicNetworkAccessType.Enabled;
}
