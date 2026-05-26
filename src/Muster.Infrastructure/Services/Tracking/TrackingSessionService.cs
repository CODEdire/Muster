using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>
/// Channel-activity tracking sessions. While a session is active, members' voice presence in its
/// channel accumulates; closing the session awards points proportional to minutes attended. Sessions
/// are opened manually by an admin or bound to a Discord scheduled event.
/// </summary>
public class TrackingSessionService(MusterDbContext db, ICurrencyService awards, GuildAuthorizationService auth)
{
    public const int DefaultPointsPerMinute = 1;

    public async Task<TrackingSession> OpenManualAsync(
        ulong guildId, ulong voiceChannelId, ulong openedBy, CancellationToken ct = default)
        => await OpenAsync(guildId, voiceChannelId, TrackingSessionSource.Manual, scheduledEventId: null, openedBy, ct);

    public async Task<TrackingSession> OpenForScheduledEventAsync(
        ulong guildId, ulong voiceChannelId, ulong scheduledEventId, CancellationToken ct = default)
        => await OpenAsync(guildId, voiceChannelId, TrackingSessionSource.DiscordScheduledEvent, scheduledEventId, openedBy: 0, ct);

    /// <summary>Open a session bound to a scheduled event, unless one is already active for it.</summary>
    public async Task<TrackingSession?> EnsureForScheduledEventAsync(
        ulong guildId, ulong voiceChannelId, ulong scheduledEventId, CancellationToken ct = default)
    {
        var alreadyOpen = await db.HasActiveSessionForEventAsync(guildId, scheduledEventId, ct);
        if (alreadyOpen)
        {
            return null;
        }

        return await OpenAsync(guildId, voiceChannelId, TrackingSessionSource.DiscordScheduledEvent, scheduledEventId, openedBy: 0, ct);
    }

    /// <summary>Close the active session bound to a scheduled event, if any.</summary>
    public async Task CloseForScheduledEventAsync(ulong guildId, ulong scheduledEventId, CancellationToken ct = default)
    {
        var session = await db.FindActiveSessionForEventAsync(guildId, scheduledEventId, ct);
        if (session is not null)
        {
            await CloseAsync(session.Id, ct: ct);
        }
    }

    private async Task<TrackingSession> OpenAsync(
        ulong guildId, ulong voiceChannelId, TrackingSessionSource source, ulong? scheduledEventId,
        ulong openedBy, CancellationToken ct)
    {
        var session = new TrackingSession
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Source = source,
            ScheduledEventId = scheduledEventId,
            VoiceChannelId = voiceChannelId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = TrackingSessionStatus.Active,
            OpenedBy = openedBy,
        };
        db.TrackingSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// Reconcile a member's voice presence against active sessions: close any open segment in a
    /// session they've left, and open one in the session for the channel they're now in.
    /// </summary>
    public async Task ProcessVoiceStateAsync(
        ulong guildId, ulong userId, ulong? currentChannelId, DateTimeOffset? at = null, CancellationToken ct = default)
    {
        var now = at ?? DateTimeOffset.UtcNow;

        var activeSessions = await db.ListActiveSessionsAsync(guildId, ct);

        foreach (var session in activeSessions)
        {
            var attendance = await db.FindAttendanceAsync(session.Id, userId, ct);
            var isCurrent = currentChannelId == session.VoiceChannelId;

            if (isCurrent)
            {
                if (attendance is null)
                {
                    attendance = new VoiceAttendance
                    {
                        Id = Guid.NewGuid(),
                        TrackingSessionId = session.Id,
                        UserId = userId,
                        FirstJoinedAt = now,
                    };
                    db.VoiceAttendance.Add(attendance);
                }

                attendance.OpenSegmentStart ??= now;
            }
            else if (attendance?.OpenSegmentStart is { } start)
            {
                CloseSegment(attendance, start, now);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Close a session: finalize open presence segments and award points by minutes attended.
    /// When <paramref name="pointsPerMinute"/> is null the guild's configured rate is used.
    /// Returns false if no such session exists (idempotent: closing an already-closed session returns true).
    /// </summary>
    public async Task<bool> CloseAsync(
        Guid sessionId, DateTimeOffset? at = null, int? pointsPerMinute = null, CancellationToken ct = default)
    {
        var now = at ?? DateTimeOffset.UtcNow;

        var session = await db.FindSessionWithAttendanceAsync(sessionId, ct);

        if (session is null)
        {
            return false;
        }

        if (session.Status == TrackingSessionStatus.Closed)
        {
            return true;
        }

        session.Status = TrackingSessionStatus.Closed;
        session.EndedAt = now;

        foreach (var attendance in session.Attendance)
        {
            if (attendance.OpenSegmentStart is { } start)
            {
                CloseSegment(attendance, start, now);
            }
        }

        await db.SaveChangesAsync(ct);

        var rate = pointsPerMinute ?? await ResolvePointsPerMinuteAsync(session.GuildId, ct);

        foreach (var attendance in session.Attendance.Where(a => a.TotalMinutes > 0))
        {
            // Attendance is still recorded for everyone, but only eligible participants are rewarded.
            if (!await auth.IsParticipantAsync(session.GuildId, attendance.UserId, ct))
            {
                continue;
            }

            await awards.AwardPointsAsync(
                session.GuildId, attendance.UserId, attendance.TotalMinutes * rate,
                LedgerSourceType.TrackingSession, $"session:{sessionId}:user:{attendance.UserId}",
                "Voice attendance", ct);
        }

        return true;
    }

    private async Task<int> ResolvePointsPerMinuteAsync(ulong guildId, CancellationToken ct)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        var rate = guild?.Settings.PointsPerVoiceMinute ?? DefaultPointsPerMinute;
        return rate > 0 ? rate : DefaultPointsPerMinute;
    }

    private static void CloseSegment(VoiceAttendance attendance, DateTimeOffset start, DateTimeOffset end)
    {
        attendance.TotalMinutes += (int)(end - start).TotalMinutes;
        attendance.OpenSegmentStart = null;
        attendance.LastLeftAt = end;
    }
}
