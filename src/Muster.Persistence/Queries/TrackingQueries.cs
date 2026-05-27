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

    /// <summary>All active sessions in a guild with attendance loaded (for the session reconcile engine).</summary>
    public static Task<List<TrackingSession>> ListActiveSessionsWithAttendanceAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.TrackingSessions.Include(s => s.Attendance)
            .Where(s => s.GuildId == guildId && s.Status == TrackingSessionStatus.Active).ToListAsync(ct);

    /// <summary>Guild ids that currently have an active session (the sweep reconciles these).</summary>
    public static Task<List<ulong>> ListGuildIdsWithActiveSessionsAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.TrackingSessions.Where(s => s.Status == TrackingSessionStatus.Active)
            .Select(s => s.GuildId).Distinct().ToListAsync(ct);

    /// <summary>Every active session across all guilds (the stale-session sweep checks these against MaxSessionHours).</summary>
    public static Task<List<TrackingSession>> ListAllActiveSessionsAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.TrackingSessions.Where(s => s.Status == TrackingSessionStatus.Active).ToListAsync(ct);

    /// <summary>A member's background presence rows in a guild (opt-out eviction deletes these).</summary>
    public static Task<List<BackgroundVoicePresence>> ListBackgroundPresencesForMemberAsync(this MusterDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
        => db.BackgroundVoicePresences.Where(p => p.GuildId == guildId && p.UserId == userId).ToListAsync(ct);

    /// <summary>A member's attendance rows in the guild's currently-active sessions (opt-out eviction deletes these).</summary>
    public static Task<List<VoiceAttendance>> ListActiveAttendanceForMemberAsync(this MusterDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
        => (from a in db.VoiceAttendance
            join s in db.TrackingSessions on a.TrackingSessionId equals s.Id
            where s.GuildId == guildId && s.Status == TrackingSessionStatus.Active && a.UserId == userId
            select a).ToListAsync(ct);

    /// <summary>Attendance rows with an open segment (startup voids these — stale after a restart).</summary>
    public static Task<List<VoiceAttendance>> ListOpenAttendanceAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.VoiceAttendance.Where(a => a.OpenSegmentStart != null).ToListAsync(ct);

    /// <summary>A user's attendance row in a session, if any.</summary>
    public static Task<VoiceAttendance?> FindAttendanceAsync(this MusterDbContext db, Guid sessionId, ulong userId, CancellationToken ct = default)
        => db.VoiceAttendance.FirstOrDefaultAsync(a => a.TrackingSessionId == sessionId && a.UserId == userId, ct);

    /// <summary>All attendance rows for a session (the reconcile working set — queried, not via the nav collection,
    /// so a reused DbContext can't double-count via Include fixup).</summary>
    public static Task<List<VoiceAttendance>> AttendanceForSessionAsync(this MusterDbContext db, Guid sessionId, CancellationToken ct = default)
        => db.VoiceAttendance.Where(a => a.TrackingSessionId == sessionId).ToListAsync(ct);

    /// <summary>A session with its attendance loaded (the full session aggregate, for close).</summary>
    public static Task<TrackingSession?> FindSessionWithAttendanceAsync(this MusterDbContext db, Guid sessionId, CancellationToken ct = default)
        => db.TrackingSessions.Include(s => s.Attendance).FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    /// <summary>Whether a bounded session is active on a voice channel — background accrual yields to it (Session wins).</summary>
    public static Task<bool> HasActiveSessionForChannelAsync(this MusterDbContext db, ulong guildId, ulong channelId, CancellationToken ct = default)
        => db.TrackingSessions.AnyAsync(
            s => s.GuildId == guildId && s.VoiceChannelId == channelId && s.Status == TrackingSessionStatus.Active, ct);

    // --- Background tracking (per-channel config + always-on voice accrual) ---

    /// <summary>A channel's monitoring rule, if configured.</summary>
    public static Task<TrackedChannel?> FindTrackedChannelAsync(this MusterDbContext db, ulong guildId, ulong channelId, CancellationToken ct = default)
        => db.TrackedChannels.FirstOrDefaultAsync(c => c.GuildId == guildId && c.ChannelId == channelId, ct);

    /// <summary>All of a guild's channel monitoring rules.</summary>
    public static Task<List<TrackedChannel>> ListTrackedChannelsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.TrackedChannels.Where(c => c.GuildId == guildId).ToListAsync(ct);

    /// <summary>A guild's monitored voice channels (Mode != Off) — reconcile accrues active-time for all of
    /// these and reward only for the Reward-mode subset.</summary>
    public static Task<List<TrackedChannel>> ListVoiceTrackedChannelsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.TrackedChannels
            .Where(c => c.GuildId == guildId && c.Kind == TrackedChannelKind.Voice && c.Mode != TrackedChannelMode.Off)
            .ToListAsync(ct);

    /// <summary>All background presence rows for a channel (reconcile reads these to award/close per member).</summary>
    public static Task<List<BackgroundVoicePresence>> ListPresencesForChannelAsync(this MusterDbContext db, ulong guildId, ulong channelId, CancellationToken ct = default)
        => db.BackgroundVoicePresences.Where(p => p.GuildId == guildId && p.ChannelId == channelId).ToListAsync(ct);

    /// <summary>Every background presence with an open accrual segment, reward or active (startup voids these — stale after a restart).</summary>
    public static Task<List<BackgroundVoicePresence>> ListOpenBackgroundPresencesAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.BackgroundVoicePresences.Where(p => p.OpenSegmentStart != null || p.ActiveOpenSegmentStart != null).ToListAsync(ct);

    /// <summary>Guild ids that have at least one monitored voice channel (the flush sweep only visits these).</summary>
    public static Task<List<ulong>> ListGuildIdsWithVoiceTrackingAsync(this MusterDbContext db, CancellationToken ct = default)
        => db.TrackedChannels
            .Where(c => c.Kind == TrackedChannelKind.Voice && c.Mode != TrackedChannelMode.Off)
            .Select(c => c.GuildId).Distinct().ToListAsync(ct);

    /// <summary>A member's season participation accumulator for a season, if it exists.</summary>
    public static Task<SeasonParticipation?> FindSeasonParticipationAsync(
        this MusterDbContext db, ulong guildId, ulong userId, Guid seasonId, CancellationToken ct = default)
        => db.SeasonParticipations.FirstOrDefaultAsync(
            p => p.GuildId == guildId && p.UserId == userId && p.SeasonId == seasonId, ct);

    /// <summary>A single member's tracking/privacy preference (Default when the member isn't tracked yet).</summary>
    public static async Task<TrackingChoice> MemberTrackingChoiceAsync(
        this MusterDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
        => await db.GuildMembers.Where(m => m.GuildId == guildId && m.UserId == userId)
            .Select(m => m.Tracking).FirstOrDefaultAsync(ct);

    /// <summary>Tracking/privacy preferences for a set of the guild's members, keyed by user id (absent = Default).</summary>
    public static async Task<Dictionary<ulong, TrackingChoice>> TrackingChoicesAsync(
        this MusterDbContext db, ulong guildId, IReadOnlyCollection<ulong> userIds, CancellationToken ct = default)
        => await db.GuildMembers
            .Where(m => m.GuildId == guildId && userIds.Contains(m.UserId))
            .ToDictionaryAsync(m => m.UserId, m => m.Tracking, ct);
}
