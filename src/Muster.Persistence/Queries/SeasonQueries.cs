using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Persistence.Queries;

/// <summary>A season for the season picker / season-over-season views.</summary>
public record SeasonInfo(Guid Id, string Name, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, bool IsActive);

/// <summary>Queries over scoring seasons.</summary>
public static class SeasonQueries
{
    /// <summary>All of a guild's seasons, newest first (active flagged) — for the season picker.</summary>
    public static async Task<List<SeasonInfo>> SeasonsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
    {
        var rows = await db.Seasons
            .Where(s => s.GuildId == guildId)
            .OrderByDescending(s => s.StartsAt)
            .Select(s => new { s.Id, s.Name, s.StartsAt, s.EndsAt, s.Status })
            .ToListAsync(ct);

        return rows.Select(s => new SeasonInfo(s.Id, s.Name, s.StartsAt, s.EndsAt, s.Status == SeasonStatus.Active)).ToList();
    }

    /// <summary>A member's total per season for one currency (sum of ledger amounts grouped by season) — the
    /// season-over-season chart. Only entries tagged with a season (i.e. seasonal currencies) contribute.</summary>
    public static async Task<Dictionary<Guid, long>> MemberSeasonTotalsAsync(
        this MusterDbContext db, ulong guildId, ulong userId, Guid currencyId, CancellationToken ct = default)
    {
        var rows = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId != null)
            .GroupBy(e => e.SeasonId!.Value)
            .Select(g => new { SeasonId = g.Key, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.SeasonId, r => r.Total);
    }

    /// <summary>The guild's active season, or null if none is open.</summary>
    public static Task<Season?> FindActiveSeasonAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Seasons.FirstOrDefaultAsync(s => s.GuildId == guildId && s.Status == SeasonStatus.Active, ct);

    /// <summary>The id of the guild's active season (scalar), or null.</summary>
    public static async Task<Guid?> ActiveSeasonIdAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => await db.Seasons.Where(s => s.GuildId == guildId && s.Status == SeasonStatus.Active)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);

    /// <summary>Whether the guild has an active season.</summary>
    public static Task<bool> HasActiveSeasonAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Seasons.AnyAsync(s => s.GuildId == guildId && s.Status == SeasonStatus.Active, ct);
}
