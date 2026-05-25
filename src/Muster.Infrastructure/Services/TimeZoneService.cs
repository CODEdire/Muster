using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;

namespace Muster.Infrastructure.Services;

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
        var userTz = await db.Users.Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct);
        if (IsValidZone(userTz))
        {
            return userTz!;
        }

        var guildTz = await db.Guilds.Where(g => g.Id == guildId).Select(g => g.TimeZoneId).FirstOrDefaultAsync(ct);
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

    /// <summary>Parse a date/time string a user typed (e.g. "2026-06-01 18:00" or "2026-06-01") in their zone.</summary>
    public async Task<(bool Ok, DateTimeOffset? Utc, string? Error)> ParseLocalAsync(
        ulong guildId, ulong userId, string? raw, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (true, null, null);
        }

        if (!DateTime.TryParse(raw.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local))
        {
            return (false, null, $"Couldn't read the date '{raw}'. Use a format like `2026-06-01 18:00`.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(await ResolveZoneIdAsync(guildId, userId, ct));
        return (true, LocalToUtc(local, zone), null);
    }

    /// <summary>Store (or clear, when null/blank) a user's preferred zone. Validates the id.</summary>
    public async Task<(bool Ok, string? Error)> SetUserZoneAsync(ulong userId, string? zoneId, CancellationToken ct = default)
    {
        var trimmed = zoneId?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && !IsValidZone(trimmed))
        {
            return (false, $"'{trimmed}' isn't a recognized time zone. Try an IANA id like `America/New_York` or `Europe/London`.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
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
