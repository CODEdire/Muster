using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

/// <summary>
/// Manages scoring seasons. Starting a new season archives the current one, so seasonal POINTS
/// leaderboards reset while spendable currencies (which aren't season-scoped) persist.
/// </summary>
public class SeasonService(MusterDbContext db)
{
    public async Task<Season?> GetActiveAsync(ulong guildId, CancellationToken ct = default)
        => await db.Seasons.FirstOrDefaultAsync(s => s.GuildId == guildId && s.Status == SeasonStatus.Active, ct);

    /// <summary>Archive the current active season (if any) and open a new one.</summary>
    public async Task<Season> StartAsync(ulong guildId, string name, CancellationToken ct = default)
    {
        await ArchiveActiveAsync(guildId, ct);

        var season = new Season
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Name = name,
            StartsAt = DateTimeOffset.UtcNow,
            Status = SeasonStatus.Active,
        };
        db.Seasons.Add(season);
        await db.SaveChangesAsync(ct);
        return season;
    }

    /// <summary>End the active season without opening a new one. Returns the archived season, or null if none.</summary>
    public async Task<Season?> EndAsync(ulong guildId, CancellationToken ct = default)
    {
        var ended = await ArchiveActiveAsync(guildId, ct);
        if (ended is not null)
        {
            await db.SaveChangesAsync(ct);
        }

        return ended;
    }

    private async Task<Season?> ArchiveActiveAsync(ulong guildId, CancellationToken ct)
    {
        var active = await GetActiveAsync(guildId, ct);
        if (active is null)
        {
            return null;
        }

        active.Status = SeasonStatus.Archived;
        active.EndsAt = DateTimeOffset.UtcNow;
        return active;
    }
}
