using System;
using System.Collections.Generic;

namespace Muster.AppHost.Core;

/// <summary>
/// Maps an Azure region name to its short CAF-style abbreviation for use in resource names
/// (e.g. <c>southcentralus</c> → <c>scus</c>). azd exports the canonical region name as the
/// <c>AZURE_LOCATION</c> environment variable; this turns it into the slug the naming convention wants.
///
/// <para>Core US regions only for now — extend <see cref="Map"/> as other regions come online.</para>
/// </summary>
internal static class AzureLocationAbbreviations
{
    // Accepts both the canonical name ("southcentralus") and the display name ("South Central US");
    // input is normalised (lowercased, spaces stripped) before lookup.
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["eastus"] = "eus",
        ["eastus2"] = "eus2",
        ["centralus"] = "cus",
        ["northcentralus"] = "ncus",
        ["southcentralus"] = "scus",
        ["westcentralus"] = "wcus",
        ["westus"] = "wus",
        ["westus2"] = "wus2",
        ["westus3"] = "wus3",
    };

    /// <summary>Returns the abbreviation for <paramref name="location"/>, or throws if the region is
    /// not yet mapped (so an unsupported region fails loudly at provisioning rather than producing a
    /// garbage name).</summary>
    public static string ToAbbreviation(this string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);

        string key = location.Replace(" ", "").ToLowerInvariant();

        return Map.TryGetValue(key, out string? abbr)
            ? abbr
            : throw new InvalidOperationException(
                $"No location abbreviation mapped for Azure region '{location}'. " +
                $"Add it to {nameof(AzureLocationAbbreviations)}.{nameof(Map)}.");
    }
}
