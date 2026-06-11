using Microsoft.Extensions.Configuration;
using Microsoft.FeatureManagement;
using Muster.Contracts;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Infrastructure.Services.Platform;

/// <summary>Maps a <see cref="PlatformFeature"/> to its feature-management flag name (as defined in Azure App
/// Configuration / appsettings). Kept separate from the enum name so the wire label can differ from the code.</summary>
public static class PlatformFeatureNames
{
    public static string Of(PlatformFeature feature) => feature switch
    {
        PlatformFeature.Shop => "MusterShop",
        PlatformFeature.Quests => "MusterQuests",
        _ => feature.ToString(),
    };
}

/// <summary>
/// Decides whether a <see cref="PlatformFeature"/> is on for a guild — queried like authorization. The verdict is
/// the most-restrictive of three stacked layers, evaluated top-down:
/// <list type="number">
/// <item><b>Platform</b> (<see cref="IPlatformFeatureSource"/>) — a global kill-switch from feature management /
/// app config (appsettings is the default backing store). Off ⇒ <see cref="FeatureAvailability.Unavailable"/>.</item>
/// <item><b>Billing</b> (<see cref="IFeatureEntitlementSource"/>) — does the guild's plan include it? Not entitled
/// ⇒ <see cref="FeatureAvailability.Unavailable"/>. (Stubbed allow-all until billing ships.)</item>
/// <item><b>Guild</b> (<see cref="IGuildFeatureSource"/>) — the per-guild on/off toggle. On ⇒
/// <see cref="FeatureAvailability.Enabled"/>; off ⇒ <see cref="FeatureAvailability.Available"/> (an admin may enable
/// it).</item>
/// </list>
/// </summary>
public interface IFeatureGate
{
    /// <summary>The full verdict (tri-state + deciding layer) for one feature in one guild.</summary>
    Task<FeatureVerdict> EvaluateAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default);

    /// <summary>Convenience: true only when the feature is fully <see cref="FeatureAvailability.Enabled"/>.</summary>
    Task<bool> IsEnabledAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default);
}

/// <summary>Platform-wide kill-switch layer (feature management / app config). Guild-agnostic.</summary>
public interface IPlatformFeatureSource
{
    Task<bool> IsAvailableAsync(PlatformFeature feature, CancellationToken ct = default);
}

/// <summary>Billing-entitlement layer: does the guild's plan include the feature?</summary>
public interface IFeatureEntitlementSource
{
    Task<bool> IsEntitledAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default);
}

/// <summary>Per-guild on/off layer: has the guild turned the feature on?</summary>
public interface IGuildFeatureSource
{
    Task<bool> IsEnabledAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default);
}

public sealed class FeatureGate(
    IPlatformFeatureSource platform,
    IFeatureEntitlementSource billing,
    IGuildFeatureSource guild) : IFeatureGate
{
    public async Task<FeatureVerdict> EvaluateAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default)
    {
        if (!await platform.IsAvailableAsync(feature, ct))
        {
            return new(FeatureAvailability.Unavailable, FeatureGateReason.PlatformDisabled);
        }

        if (!await billing.IsEntitledAsync(guildId, feature, ct))
        {
            return new(FeatureAvailability.Unavailable, FeatureGateReason.NotEntitled);
        }

        return await guild.IsEnabledAsync(guildId, feature, ct)
            ? new(FeatureAvailability.Enabled, FeatureGateReason.Enabled)
            : new(FeatureAvailability.Available, FeatureGateReason.GuildDisabled);
    }

    public async Task<bool> IsEnabledAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default)
        => (await EvaluateAsync(guildId, feature, ct)).IsEnabled;
}

/// <summary>
/// Platform layer backed by <see cref="IConfiguration"/> — reads <c>FeatureManagement:{Feature}</c> (appsettings,
/// environment, or a bound provider like Azure App Configuration). Defaults to <b>on</b> when unset, so adopting the
/// gate never silently disables a shipped feature; set the flag to <c>false</c> to kill it platform-wide. A host can
/// swap this for the Microsoft.FeatureManagement library behind the same interface without touching callers.
/// </summary>
public sealed class ConfigurationFeatureSource(IConfiguration config) : IPlatformFeatureSource
{
    public Task<bool> IsAvailableAsync(PlatformFeature feature, CancellationToken ct = default)
        => Task.FromResult(config.GetValue<bool?>($"FeatureManagement:{feature}") ?? true);
}

/// <summary>
/// Platform layer backed by Microsoft.FeatureManagement (<see cref="IFeatureManager"/>) — the live source, with the
/// flag named via <see cref="PlatformFeatureNames"/> (Shop ⇒ <c>MusterShop</c>) in Azure App Configuration, falling
/// back to the <c>FeatureManagement</c> section of appsettings. Feature management treats an <i>undefined</i> flag as
/// off; we invert that to <b>on</b> so adopting the gate before the flag exists never silently kills a feature — only
/// an explicitly-defined flag set to <c>false</c> disables it.
/// </summary>
public sealed class FeatureManagementPlatformSource(IFeatureManager features) : IPlatformFeatureSource
{
    public async Task<bool> IsAvailableAsync(PlatformFeature feature, CancellationToken ct = default)
    {
        var name = PlatformFeatureNames.Of(feature);
        await foreach (var defined in features.GetFeatureNamesAsync().WithCancellation(ct))
        {
            if (string.Equals(defined, name, StringComparison.OrdinalIgnoreCase))
            {
                return await features.IsEnabledAsync(name);
            }
        }

        return true; // flag not defined yet ⇒ feature on
    }
}

/// <summary>Billing not built yet — every guild is entitled to everything. Replace when billing lands.</summary>
public sealed class AllowAllEntitlementSource : IFeatureEntitlementSource
{
    public Task<bool> IsEntitledAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>
/// Per-guild toggle layer. Maps each feature to its existing guild switch (no new storage): Shop ⇒
/// <c>GuildShopSettings.PlayerMarketEnabled</c>, Quests ⇒ <c>GuildQuestSettings.QuestsEnabled</c>. Features without an
/// explicit per-guild toggle are on once the platform + plan allow them.
/// </summary>
public sealed class GuildFeatureSource(GuildShopSettingsService shopSettings, GuildQuestSettingsService questSettings) : IGuildFeatureSource
{
    public async Task<bool> IsEnabledAsync(ulong guildId, PlatformFeature feature, CancellationToken ct = default) => feature switch
    {
        PlatformFeature.Shop => (await shopSettings.GetAsync(guildId, ct)).PlayerMarketEnabled,
        PlatformFeature.Quests => (await questSettings.GetAsync(guildId, ct)).QuestsEnabled,
        _ => true,
    };
}
