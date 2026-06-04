namespace Muster.AppHost.Options;

public record KeyVaultOptions
{
    public bool UseExisting { get; init; } = false;

    public bool EnableRbacAuthorization { get; init; } = true;

    public bool EnableSoftDelete { get; init; } = true;

    public int SoftDeleteRetentionInDays { get; init; } = 7;

    public bool EnablePurgeProtection { get; init; } = true;

    // Key Vault's PublicNetworkAccess is a plain string ("Enabled"/"Disabled") on KeyVaultProperties.
    public string PublicNetworkAccess { get; init; } = "Enabled";
}
