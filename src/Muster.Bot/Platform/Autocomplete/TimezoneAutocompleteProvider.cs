using Muster.Bot.Platform.Telemetry;
using Muster.Infrastructure.Services.Platform;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Platform.Autocomplete;

/// <summary>
/// Suggests IANA time zones for the <c>/timezone</c> command so users pick a valid zone instead of guessing the
/// id. Sourced from the .NET system time-zone provider (<see cref="TimeZoneService.GetKnownZones"/>), cached once;
/// filtered by the user's typing against id or display.
/// </summary>
public class TimezoneAutocompleteProvider : IAutocompleteProvider<AutocompleteInteractionContext>
{
    private static readonly IReadOnlyList<(string Id, string Display)> Zones = TimeZoneService.GetKnownZones();
    private static readonly Dictionary<string, string> DisplayById =
        Zones.ToDictionary(z => z.Id, z => z.Display, StringComparer.OrdinalIgnoreCase);

    // Discord shows the first 25 results when the box is empty. The full list is ordered by UTC offset, so a naive
    // "first 25" would be all the Americas — instead seed a spread of common zones across regions until the user types.
    // Empty-input fallback. Each entry must resolve through TimeZoneService.GetKnownZones() on every supported
    // host OS — .NET's tz provider on Windows uses a subset of IANA ids (legacy names like Asia/Calcutta in
    // place of Asia/Kolkata, and some cities aren't represented when an equivalent-offset entry already exists).
    // Pinned by TimezoneAutocompleteProviderTests.Common_AllIdsExistInZones.
    private static readonly string[] Common =
    [
        "UTC", "America/Los_Angeles", "America/Denver", "America/Chicago", "America/New_York",
        "America/Mexico_City", "America/Sao_Paulo", "Europe/London", "Europe/Paris", "Europe/Berlin",
        "Europe/Moscow", "Africa/Cairo", "Africa/Johannesburg", "Asia/Dubai", "Asia/Calcutta", "Asia/Bangkok",
        "Asia/Shanghai", "Asia/Tokyo", "Asia/Seoul", "Asia/Singapore", "Australia/Sydney", "Pacific/Auckland",
    ];

    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        using var _ = BotTelemetry.MeasureAutocomplete(nameof(TimezoneAutocompleteProvider));

        var input = (option.Value ?? string.Empty).Trim();

        IEnumerable<(string Id, string Display)> source = input.Length == 0
            ? Common.Where(DisplayById.ContainsKey).Select(id => (id, DisplayById[id]))
            : Zones.Where(z => z.Id.Contains(input, StringComparison.OrdinalIgnoreCase)
                || z.Display.Contains(input, StringComparison.OrdinalIgnoreCase));

        var matches = source
            .Take(25)
            .Select(z => new ApplicationCommandOptionChoiceProperties(
                z.Display.Length > 100 ? z.Display[..100] : z.Display, z.Id));

        return ValueTask.FromResult<IEnumerable<ApplicationCommandOptionChoiceProperties>>(matches.ToList());
    }
}
