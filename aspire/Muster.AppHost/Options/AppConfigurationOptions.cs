using Azure.Provisioning.AppConfiguration;

namespace Muster.AppHost.Options;

public record AppConfigurationOptions
{
    // "Free" or "Standard". Free is the no-cost default; soft-delete + purge protection are Standard-only.
    public string SkuName { get; init; } = "Free";

    public bool UseExisting { get; init; } = false;

    // Nullable: leave unset on Free (Azure rejects them there). Set only on Standard. See Validate().
    public int? SoftDeleteRetentionInDays { get; init; }

    public bool? EnablePurgeProtection { get; init; }

    public bool DisableLocalAuth { get; init; } = true;

    public AppConfigurationPublicNetworkAccess PublicNetworkAccess { get; init; } = AppConfigurationPublicNetworkAccess.Enabled;

    private bool IsStandard => string.Equals(SkuName, "Standard", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fail fast on SKU/feature mismatches Azure would reject at provision time. App Configuration
    /// soft-delete (and therefore purge protection) is a Standard-tier feature only — leave both unset on Free.</summary>
    public void Validate()
    {
        if (EnablePurgeProtection is not null && !IsStandard)
        {
            throw new InvalidOperationException(
                $"AppConfigurationOptions: EnablePurgeProtection requires the Standard SKU (got '{SkuName}'). " +
                "Leave it unset for Free, or set SkuName=Standard.");
        }

        if (SoftDeleteRetentionInDays is not null && !IsStandard)
        {
            throw new InvalidOperationException(
                $"AppConfigurationOptions: SoftDeleteRetentionInDays requires the Standard SKU (got '{SkuName}'). " +
                "Leave it unset for Free, or set SkuName=Standard.");
        }
    }
}
