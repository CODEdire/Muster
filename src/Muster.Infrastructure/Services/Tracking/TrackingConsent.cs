using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>What a member's preference + the guild policy resolve to.</summary>
public readonly record struct TrackingConsent(bool Background, bool Sessions);

/// <summary>Resolves a member's <see cref="TrackingChoice"/> against the guild's background policy.</summary>
public static class TrackingConsentResolver
{
    public static TrackingConsent Resolve(TrackingChoice choice, bool guildBackgroundOptIn) => choice switch
    {
        TrackingChoice.AllOut => new(Background: false, Sessions: false),
        TrackingChoice.BackgroundOut => new(Background: false, Sessions: true),
        TrackingChoice.In => new(Background: true, Sessions: true),
        // Default: Sessions always tracked; background follows the guild policy (opt-in => off until opted in).
        _ => new(Background: !guildBackgroundOptIn, Sessions: true),
    };
}
