using System.Reflection;
using Muster.Bot.Platform.Autocomplete;
using Xunit;

namespace Muster.Bot.UnitTests.Platform.Autocomplete;

/// <summary>
/// Static-data sanity for the timezone autocomplete. The actual NetCord-bound
/// <c>GetChoicesAsync</c> needs an <c>AutocompleteInteractionContext</c> (sealed, internal ctor) so it's
/// integration territory — but the cached <c>Zones</c> / <c>Common</c> tables are pure and worth pinning:
/// they're what users see when they type, and a regression here (missing UTC, Common ids that don't exist
/// in Zones, etc.) shows up as silently empty Discord suggestions.
/// </summary>
public class TimezoneAutocompleteProviderTests
{
    private static IReadOnlyList<(string Id, string Display)> Zones()
    {
        var field = typeof(TimezoneAutocompleteProvider)
            .GetField("Zones", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IReadOnlyList<(string, string)>)field.GetValue(null)!;
    }

    private static string[] Common()
    {
        var field = typeof(TimezoneAutocompleteProvider)
            .GetField("Common", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string[])field.GetValue(null)!;
    }

    [Fact]
    public void Zones_LoadedFromSystem_HasMany()
    {
        var zones = Zones();
        Assert.True(zones.Count > 50, $"Expected many IANA zones from the system tz provider, got {zones.Count}.");
    }

    [Fact]
    public void Zones_IncludesUtc()
    {
        var zones = Zones();
        Assert.Contains(zones, z => z.Id == "UTC");
    }

    [Fact]
    public void Zones_NoEmptyDisplayStrings()
    {
        var zones = Zones();
        Assert.All(zones, z =>
        {
            Assert.False(string.IsNullOrWhiteSpace(z.Id));
            Assert.False(string.IsNullOrWhiteSpace(z.Display));
        });
    }

    [Fact]
    public void Common_IsNonEmpty_AndIncludesUtc()
    {
        var common = Common();
        Assert.NotEmpty(common);
        Assert.Contains("UTC", common);
    }

    [Fact]
    public void Common_FitsDiscord25ChoiceLimit()
    {
        // Discord caps autocomplete results at 25. The empty-input fallback emits Common, so it can't exceed 25.
        Assert.True(Common().Length <= 25, $"Common has {Common().Length} entries; Discord caps autocomplete at 25.");
    }

    [Fact]
    public void Common_AllIdsExistInZones()
    {
        // Empty-input fallback filters Common through Zones.ContainsKey; an unknown id would silently disappear
        // from suggestions. Catch that at test time.
        var ids = Zones().Select(z => z.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = Common().Where(c => !ids.Contains(c)).ToList();
        Assert.True(missing.Count == 0,
            "Common ids must all exist in Zones (case-insensitive). Missing: " + string.Join(", ", missing));
    }
}
