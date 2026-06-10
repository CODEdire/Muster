namespace Muster.Contracts;

/// <summary>
/// A platform capability that can be turned on/off through the layered <c>IFeatureGate</c> (platform kill-switch →
/// billing entitlement → per-guild toggle). Add a member here as each feature adopts the gate.
/// </summary>
public enum PlatformFeature
{
    /// <summary>The player marketplace (stores, listings, orders, offers, disputes, ratings).</summary>
    Shop,
}

/// <summary>
/// Tri-state outcome of a feature-gate evaluation. The most restrictive layer wins, so callers can both decide
/// "can I use this right now?" (<see cref="Enabled"/>) and "should I show an upsell vs a simple enable toggle vs
/// nothing?" (<see cref="Available"/> vs <see cref="Unavailable"/>).
/// </summary>
public enum FeatureAvailability
{
    /// <summary>Blocked above the guild — the platform kill-switch is off, or the guild's plan doesn't include it.
    /// The guild can neither use nor enable it.</summary>
    Unavailable = 0,

    /// <summary>Permitted by the platform and the guild's plan, but the guild hasn't turned it on. An admin can
    /// enable it.</summary>
    Available = 1,

    /// <summary>On and usable right now.</summary>
    Enabled = 2,
}

/// <summary>Which layer decided the verdict — drives precise UI messaging (kill-switch vs upsell vs toggle).</summary>
public enum FeatureGateReason
{
    Enabled = 0,
    /// <summary>The platform-wide kill-switch (feature management / app config) is off.</summary>
    PlatformDisabled,
    /// <summary>The guild's billing plan doesn't entitle this feature.</summary>
    NotEntitled,
    /// <summary>Platform + plan allow it, but the guild has it switched off.</summary>
    GuildDisabled,
}

/// <summary>The result of gating one feature for one guild: the tri-state plus the deciding layer.</summary>
public readonly record struct FeatureVerdict(FeatureAvailability Availability, FeatureGateReason Reason)
{
    public bool IsEnabled => Availability == FeatureAvailability.Enabled;

    /// <summary>True when the platform/plan permit the feature even if the guild has it off — i.e. an admin
    /// could turn it on. False only when a layer above the guild blocks it.</summary>
    public bool CanEnable => Availability != FeatureAvailability.Unavailable;
}
