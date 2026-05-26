using System.Text.RegularExpressions;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;

namespace Muster.Infrastructure.Services.Platform;

/// <summary>
/// Resolves the time zone to use when interpreting a date a user types, and converts between a user's
/// local wall-clock time and UTC. Resolution order: the user's stored preference → the guild's
/// configured zone → UTC. Time zones use IANA ids (e.g. "America/New_York"); .NET maps these on all
/// platforms.
/// </summary>
public class TimeZoneService(MusterDbContext db)
{
    public const string Utc = "UTC";

    /// <summary>Resolve the effective IANA zone id for a user in a guild (user pref → guild → UTC).</summary>
    public async Task<string> ResolveZoneIdAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var userTz = await db.UserTimeZoneIdAsync(userId, ct);
        if (IsValidZone(userTz))
        {
            return userTz!;
        }

        var guildTz = await db.GuildTimeZoneIdAsync(guildId, ct);
        return IsValidZone(guildTz) ? guildTz! : Utc;
    }

    /// <summary>Convert a wall-clock local time entered by the user to UTC, using their resolved zone.</summary>
    public async Task<DateTimeOffset?> LocalToUtcAsync(
        ulong guildId, ulong userId, DateTime? localWallClock, CancellationToken ct = default)
    {
        if (localWallClock is not { } local)
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(await ResolveZoneIdAsync(guildId, userId, ct));
        return LocalToUtc(local, zone);
    }

    /// <summary>True when the user has set (and still has) a valid personal zone.</summary>
    public async Task<bool> HasUserZoneAsync(ulong userId, CancellationToken ct = default)
        => IsValidZone(await db.UserTimeZoneIdAsync(userId, ct));

    /// <summary>The user's own stored zone id, or null if they haven't set one.</summary>
    public Task<string?> GetUserZoneAsync(ulong userId, CancellationToken ct = default)
        => db.UserTimeZoneIdAsync(userId, ct);

    /// <summary>
    /// Parse a date/time a user typed. Time-zone-free forms are always accepted: a Discord timestamp
    /// (<c>&lt;t:1700000000&gt;</c>), a bare unix value, or a relative duration (<c>in 3 days</c>, <c>2h</c>).
    /// A plain wall-clock date (<c>2026-06-01 18:00</c>) needs the user's <b>own</b> zone — if they haven't set one
    /// we gate (Error tells them to <c>/timezone</c> or use a timestamp/relative time) rather than silently guess.
    /// </summary>
    public async Task<(bool Ok, DateTimeOffset? Utc, string? Error)> ParseLocalAsync(
        ulong guildId, ulong userId, string? raw, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (true, null, null);
        }

        var s = raw.Trim();

        // Zone-independent forms work for everyone, configured or not.
        if (TryParseInstant(s, DateTimeOffset.UtcNow, out var instant))
        {
            return (true, instant, null);
        }

        // A plain wall-clock time must be read in the user's own zone — gate when they haven't set one.
        var userTz = await db.UserTimeZoneIdAsync(userId, ct);
        if (!IsValidZone(userTz))
        {
            return (false, null,
                "⏰ Set your time zone first with `/timezone` so I can read that date — or enter a Discord timestamp " +
                "like `<t:1700000000>` (right-click a Discord time → Copy) or a relative time like `in 3 days`.");
        }

        if (!DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local))
        {
            return (false, null, $"Couldn't read the date '{raw}'. Use `2026-06-01 18:00`, a Discord timestamp, or `in 3 days`.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(userTz!);
        return (true, LocalToUtc(local, zone), null);
    }

    /// <summary>Parse the zone-independent date forms: Discord timestamp, bare unix seconds/millis, or a relative
    /// duration from now (<c>in 3 days</c>, <c>2h</c>, <c>90m</c>, <c>1w</c>). Pure — no zone or DB needed.</summary>
    public static bool TryParseInstant(string raw, DateTimeOffset now, out DateTimeOffset utc)
    {
        utc = default;
        var s = raw.Trim();

        // Discord timestamp markup, e.g. <t:1700000000> or <t:1700000000:R>
        var t = Regex.Match(s, @"^<t:(\d{1,15})(?::[tTdDfFR])?>$");
        if (t.Success)
        {
            utc = DateTimeOffset.FromUnixTimeSeconds(long.Parse(t.Groups[1].Value));
            return true;
        }

        // Bare unix (10-digit seconds or 13-digit milliseconds).
        if (Regex.IsMatch(s, @"^\d{10,13}$"))
        {
            var n = long.Parse(s);
            utc = s.Length >= 13 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
            return true;
        }

        // Relative duration: "in 3 days", "3 days", "2h", "90m", "1w".
        var r = Regex.Match(s, @"^(?:in\s+)?(\d+)\s*(m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days|w|week|weeks)$",
            RegexOptions.IgnoreCase);
        if (r.Success)
        {
            var n = int.Parse(r.Groups[1].Value);
            var unit = char.ToLowerInvariant(r.Groups[2].Value[0]);
            TimeSpan span = unit switch
            {
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                'w' => TimeSpan.FromDays(7 * n),
                _ => TimeSpan.Zero,
            };
            if (span > TimeSpan.Zero)
            {
                utc = now + span;
                return true;
            }
        }

        return false;
    }

    /// <summary>The system's known IANA zones (id + offset-prefixed display), for pickers/autocomplete. On Windows
    /// the registry ids are converted to IANA so stored ids are consistent across platforms.</summary>
    public static IReadOnlyList<(string Id, string Display)> GetKnownZones()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zones = new List<(string Id, string Display, TimeSpan Offset)>();

        foreach (var z in TimeZoneInfo.GetSystemTimeZones())
        {
            var id = z.HasIanaId ? z.Id
                : TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana
                : z.Id;

            if (!seen.Add(id))
            {
                continue;
            }

            var o = z.BaseUtcOffset;
            var sign = o < TimeSpan.Zero ? "-" : "+";
            zones.Add((id, $"(UTC{sign}{o:hh\\:mm}) {id}", o));
        }

        return zones.OrderBy(z => z.Offset).ThenBy(z => z.Id, StringComparer.OrdinalIgnoreCase)
            .Select(z => (z.Id, z.Display)).ToList();
    }

    /// <summary>Store (or clear, when null/blank) a user's preferred zone. Validates the id.</summary>
    public async Task<(bool Ok, string? Error)> SetUserZoneAsync(ulong userId, string? zoneId, CancellationToken ct = default)
    {
        var trimmed = zoneId?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && !IsValidZone(trimmed))
        {
            return (false, $"'{trimmed}' isn't a recognized time zone. Try an IANA id like `America/New_York` or `Europe/London`.");
        }

        var user = await db.FindUserAsync(userId, ct);
        if (user is null)
        {
            user = new DiscordUser { Id = userId };
            db.Users.Add(user);
        }

        user.TimeZoneId = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public static bool IsValidZone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    private static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }
}
