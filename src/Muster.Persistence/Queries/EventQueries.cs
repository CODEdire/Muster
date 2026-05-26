using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Persistence.Queries;

/// <summary>Queries over the GuildEvent aggregate (GuildEvent + its EventAttendees).</summary>
public static class EventQueries
{
    /// <summary>Load an event with its attendees — the full aggregate.</summary>
    public static Task<GuildEvent?> FindEventAsync(this MusterDbContext db, Guid id, CancellationToken ct = default)
        => db.GuildEvents.Include(e => e.Attendees).FirstOrDefaultAsync(e => e.Id == id, ct);

    /// <summary>Open events in a guild, oldest-scheduled first.</summary>
    public static Task<List<GuildEvent>> ListOpenEventsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.GuildEvents
            .Where(e => e.GuildId == guildId && e.Status == EventStatus.Open)
            .OrderBy(e => e.ScheduledStart ?? e.CreatedAt)
            .ToListAsync(ct);
}
