using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
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
    /// Reconcile active sessions against the current voice roster (snapshot-driven, like the background plane).
    /// For each session, eligible presence in its channel accrues into the member's <see cref="VoiceAttendance"/>;
    /// when the guild's anti-AFK guards are on, muted/deafened or alone-in-channel time is paused. Idempotent and
    /// self-healing — safe to call on every voice event and on the periodic sweep.
    /// </summary>
    public async Task ReconcileSessionsAsync(
        ulong guildId, IReadOnlyDictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> occupantsByChannel,
        DateTimeOffset? at = null, CancellationToken ct = default)
    {
        var sessions = await db.ListActiveSessionsAsync(guildId, ct);
        if (sessions.Count == 0)
        {
            return;
        }

        var now = at ?? DateTimeOffset.UtcNow;
        var guardsOn = (await db.GetSettingsAsync(guildId, ct)).ApplyAfkGuardsToSessions;

        foreach (var session in sessions)
        {
            var occupants = occupantsByChannel.TryGetValue(session.VoiceChannelId, out var list) ? list : [];
            var humans = occupants.Where(o => !o.IsBot).ToList();
            var present = humans.Select(h => h.UserId).ToHashSet();
            var byUser = (await db.AttendanceForSessionAsync(session.Id, ct)).ToDictionary(a => a.UserId);

            foreach (var member in humans)
            {
                var eligible = !guardsOn || (!member.IsMutedOrDeafened && humans.Count >= 2);
                var att = GetOrCreateAttendance(session.Id, byUser, member.UserId, now);

                if (eligible)
                {
                    if (att.OpenSegmentStart is null)
                    {
                        att.OpenSegmentStart = now;
                    }
                    else
                    {
                        FlushAttendance(att, now);
                    }
                }
                else if (att.OpenSegmentStart is not null)
                {
                    FlushAttendance(att, now);
                    att.OpenSegmentStart = null;
                }
            }

            foreach (var att in byUser.Values.Where(a => !present.Contains(a.UserId) && a.OpenSegmentStart is not null))
            {
                FlushAttendance(att, now);
                att.OpenSegmentStart = null;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Clear open attendance segments on startup — a stale watermark after a restart must not credit downtime.</summary>
    public async Task<int> VoidOpenAttendanceAsync(CancellationToken ct = default)
    {
        var open = await db.ListOpenAttendanceAsync(ct);
        foreach (var att in open)
        {
            att.OpenSegmentStart = null;
            att.CarrySeconds = 0;
        }

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return open.Count;
    }

    private VoiceAttendance GetOrCreateAttendance(
        Guid sessionId, Dictionary<ulong, VoiceAttendance> byUser, ulong userId, DateTimeOffset now)
    {
        if (byUser.TryGetValue(userId, out var existing))
        {
            return existing;
        }

        var created = new VoiceAttendance
        {
            Id = Guid.NewGuid(),
            TrackingSessionId = sessionId,
            UserId = userId,
            FirstJoinedAt = now,
        };
        db.VoiceAttendance.Add(created);
        byUser[userId] = created;
        return created;
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
            FlushAttendance(attendance, now);
            attendance.OpenSegmentStart = null;
        }

        await db.SaveChangesAsync(ct);

        var rate = pointsPerMinute ?? await ResolvePointsPerMinuteAsync(session.GuildId, ct);
        var (coinCurrency, minutesPerCoin) = await ResolveSessionCoinAsync(session.GuildId, ct);

        var rewardable = session.Attendance.Where(a => a.TotalMinutes > 0).ToList();
        var choices = await db.TrackingChoicesAsync(
            session.GuildId, rewardable.Select(a => a.UserId).ToList(), ct);

        foreach (var attendance in rewardable)
        {
            // Members who opted out of all tracking aren't rewarded (or counted) even for a deliberate session.
            if (choices.GetValueOrDefault(attendance.UserId) == TrackingChoice.AllOut)
            {
                continue;
            }

            // Attendance is still recorded for everyone, but only eligible participants are rewarded.
            if (!await auth.IsParticipantAsync(session.GuildId, attendance.UserId, ct))
            {
                continue;
            }

            await awards.AwardPointsAsync(
                session.GuildId, attendance.UserId, attendance.TotalMinutes * rate,
                CurrencyLedgerSource.TrackingSession, $"session:{sessionId}:user:{attendance.UserId}",
                "Voice attendance", ct);

            // Sessions (only) also mint the guild's chosen spendable currency, by minutes / minutes-per-coin.
            if (coinCurrency is not null)
            {
                var coins = attendance.TotalMinutes / minutesPerCoin;
                if (coins > 0)
                {
                    await awards.AwardAsync(
                        session.GuildId, attendance.UserId, coinCurrency.Id, coins,
                        CurrencyLedgerSource.TrackingSession, $"session:{sessionId}:user:{attendance.UserId}:coin",
                        "Session participation", ct);
                }
            }
        }

        return true;
    }

    private async Task<int> ResolvePointsPerMinuteAsync(ulong guildId, CancellationToken ct)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        var rate = guild?.Settings.PointsPerVoiceMinute ?? DefaultPointsPerMinute;
        return rate > 0 ? rate : DefaultPointsPerMinute;
    }

    /// <summary>The spendable currency a session mints on close + its minutes-per-coin rate, or (null, 0) when
    /// unconfigured or the currency code no longer resolves.</summary>
    private async Task<(Currency? currency, int minutesPerCoin)> ResolveSessionCoinAsync(ulong guildId, CancellationToken ct)
    {
        var settings = (await db.FindGuildAsync(guildId, ct))?.Settings;
        var code = settings?.SessionCoinCurrencyCode;
        var minutesPerCoin = settings?.MinutesPerCoin ?? 0;
        if (string.IsNullOrWhiteSpace(code) || minutesPerCoin <= 0)
        {
            return (null, 0);
        }

        return (await db.FindCurrencyAsync(guildId, code, ct), minutesPerCoin);
    }

    /// <summary>Roll eligible elapsed time on the open segment into whole minutes (sub-minute remainder carries),
    /// then advance the watermark. No-op when no segment is open. Caller decides whether to keep the segment open
    /// (advanced) or close it (null the start). Restart staleness is handled by <see cref="VoidOpenAttendanceAsync"/>,
    /// so an explicit close/leave settles the true elapsed time (no per-flush clamp).</summary>
    private static void FlushAttendance(VoiceAttendance attendance, DateTimeOffset now)
    {
        if (attendance.OpenSegmentStart is not { } start)
        {
            return;
        }

        var elapsed = (int)(now - start).TotalSeconds;
        var totalSeconds = attendance.CarrySeconds + Math.Max(0, elapsed);
        var minutes = totalSeconds / 60;
        attendance.CarrySeconds = totalSeconds % 60;
        attendance.OpenSegmentStart = now;

        attendance.TotalMinutes += minutes;
        attendance.LastLeftAt = now;
    }
}
