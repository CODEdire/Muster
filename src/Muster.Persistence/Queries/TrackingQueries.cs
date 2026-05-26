using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Persistence.Queries;

/// <summary>Queries over voice-tracking sessions and attendance.</summary>
public static class TrackingQueries
{
    /// <summary>Whether an active session is already bound to a scheduled event.</summary>
    public static Task<bool> HasActiveSessionForEventAsync(this MusterDbContext db, ulong guildId, ulong scheduledEventId, CancellationToken ct = default)
        => db.TrackingSessions.AnyAsync(
            s => s.GuildId == guildId && s.ScheduledEventId == scheduledEventId && s.Status == TrackingSessionStatus.Active, ct);

    /// <summary>The active session bound to a scheduled event, if any.</summary>
    public static Task<TrackingSession?> FindActiveSessionForEventAsync(this MusterDbContext db, ulong guildId, ulong scheduledEventId, CancellationToken ct = default)
        => db.TrackingSessions.FirstOrDefaultAsync(
            s => s.GuildId == guildId && s.ScheduledEventId == scheduledEventId && s.Status == TrackingSessionStatus.Active, ct);

    /// <summary>All active sessions in a guild.</summary>
    public static Task<List<TrackingSession>> ListActiveSessionsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.TrackingSessions.Where(s => s.GuildId == guildId && s.Status == TrackingSessionStatus.Active).ToListAsync(ct);

    /// <summary>A user's attendance row in a session, if any.</summary>
    public static Task<VoiceAttendance?> FindAttendanceAsync(this MusterDbContext db, Guid sessionId, ulong userId, CancellationToken ct = default)
        => db.VoiceAttendance.FirstOrDefaultAsync(a => a.TrackingSessionId == sessionId && a.UserId == userId, ct);

    /// <summary>A session with its attendance loaded (the full session aggregate, for close).</summary>
    public static Task<TrackingSession?> FindSessionWithAttendanceAsync(this MusterDbContext db, Guid sessionId, CancellationToken ct = default)
        => db.TrackingSessions.Include(s => s.Attendance).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
}
