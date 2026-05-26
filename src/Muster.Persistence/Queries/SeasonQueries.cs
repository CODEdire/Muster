using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Persistence.Queries;

/// <summary>Queries over scoring seasons.</summary>
public static class SeasonQueries
{
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
