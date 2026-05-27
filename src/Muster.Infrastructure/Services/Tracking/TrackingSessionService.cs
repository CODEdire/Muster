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
public class TrackingSessionService(MusterDbContext db, ICurrencyService awards, GuildAuthorizationService auth, RewardMultiplierService multipliers)
{
    public const int DefaultPointsPerMinute = 1;

    /// <summary>
    /// Open a manual session. Anti-AFK guards default to <paramref name="requireUnmuted"/>/<paramref name="requireNotAlone"/>
    /// when supplied; a null falls back to the guild's <c>ApplyAfkGuardsToSessions</c> policy.
    /// </summary>
    public async Task<TrackingSession> OpenManualAsync(
        ulong guildId, ulong voiceChannelId, ulong openedBy,
        string? name = null, string? channelName = null,
        bool? requireUnmuted = null, bool? requireUndeafened = null, bool? requireNotAlone = null, CancellationToken ct = default)
        => await OpenAsync(
            guildId, voiceChannelId, channelName, TrackingSessionSource.Manual, scheduledEventId: null, openedBy,
            name ?? "Manual session", requireUnmuted, requireUndeafened, requireNotAlone, ct);

    public async Task<TrackingSession> OpenForScheduledEventAsync(
        ulong guildId, ulong voiceChannelId, ulong scheduledEventId, string? name = null, string? channelName = null, CancellationToken ct = default)
        => await OpenAsync(
            guildId, voiceChannelId, channelName, TrackingSessionSource.DiscordScheduledEvent, scheduledEventId, openedBy: 0,
            name ?? "Scheduled event", requireUnmuted: null, requireUndeafened: null, requireNotAlone: null, ct);

    /// <summary>Open a session bound to a scheduled event, unless one is already active for it.</summary>
    public async Task<TrackingSession?> EnsureForScheduledEventAsync(
        ulong guildId, ulong voiceChannelId, ulong scheduledEventId, string? name = null, string? channelName = null, CancellationToken ct = default)
    {
        var alreadyOpen = await db.HasActiveSessionForEventAsync(guildId, scheduledEventId, ct);
        if (alreadyOpen)
        {
            return null;
        }

        return await OpenForScheduledEventAsync(guildId, voiceChannelId, scheduledEventId, name, channelName, ct);
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
        ulong guildId, ulong voiceChannelId, string? channelName, TrackingSessionSource source, ulong? scheduledEventId,
        ulong openedBy, string name, bool? requireUnmuted, bool? requireUndeafened, bool? requireNotAlone, CancellationToken ct)
    {
        // A null guard defaults to the guild's session-guard policy (so scheduled events follow it). Deafened
        // (checked out) and alone are the default AFK signals; merely muted (present, can't speak) is opt-in.
        var applyGuards = (await db.GetSettingsAsync(guildId, ct)).ApplyAfkGuardsToSessions;
        var guards = AfkGuardsExtensions.Compose(
            requireUnmuted ?? false, requireUndeafened ?? applyGuards, requireNotAlone ?? applyGuards);

        var session = new TrackingSession
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Name = name,
            Source = source,
            ScheduledEventId = scheduledEventId,
            VoiceChannelId = voiceChannelId,
            VoiceChannelName = channelName ?? string.Empty,
            StartedAt = DateTimeOffset.UtcNow,
            Status = TrackingSessionStatus.Active,
            OpenedBy = openedBy,
            Guards = guards,
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

        // Members who opted out of all tracking are excluded from sessions entirely (no attendance row).
        var presentUserIds = occupantsByChannel.Values.SelectMany(v => v).Where(m => !m.IsBot).Select(m => m.UserId).Distinct().ToList();
        var choices = await db.TrackingChoicesAsync(guildId, presentUserIds, ct);
        var minTracked = (await db.GetSettingsAsync(guildId, ct)).MinTrackedSeconds;

        // Reward multipliers weight each flush by the factor active now (time window + member role).
        var mult = await multipliers.LoadAsync(guildId, ct);
        var rolesByUser = mult.IsEmpty ? [] : await db.RoleIdsByUserAsync(guildId, presentUserIds, ct);
        // Credit each segment at the regime in force when it started (boundary flushes keep a segment in one regime).
        decimal Factor(ulong userId, DateTimeOffset at) => mult.IsEmpty ? 1m : mult.Factor(MultiplierScope.Sessions, at, rolesByUser.GetValueOrDefault(userId));

        foreach (var session in sessions)
        {
            var optedOut = await db.OptedOutUserIdsAsync(session.Id, ct);
            var occupants = occupantsByChannel.TryGetValue(session.VoiceChannelId, out var list) ? list : [];
            var humans = occupants
                .Where(o => !o.IsBot && choices.GetValueOrDefault(o.UserId) != TrackingChoice.AllOut && !optedOut.Contains(o.UserId))
                .ToList();
            var present = humans.Select(h => h.UserId).ToHashSet();
            var byUser = (await db.AttendanceForSessionAsync(session.Id, ct)).ToDictionary(a => a.UserId);

            foreach (var member in humans)
            {
                var eligible = (!session.Guards.Unmuted() || !member.IsMuted)
                    && (!session.Guards.Undeafened() || !member.IsDeafened)
                    && (!session.Guards.NotAlone() || humans.Count >= 2);
                var att = GetOrCreateAttendance(session.Id, byUser, member.UserId, now);
                att.LastSeenAt = now; // present in the channel (eligible or not)

                if (eligible)
                {
                    if (att.OpenSegmentStart is null)
                    {
                        att.OpenSegmentStart = now;
                    }
                    else
                    {
                        FlushAttendance(att, now, Factor(member.UserId, att.OpenSegmentStart ?? now));
                    }
                }
                else if (att.OpenSegmentStart is not null)
                {
                    FlushAttendance(att, now, Factor(member.UserId, att.OpenSegmentStart ?? now));
                    att.OpenSegmentStart = null;
                }
            }

            foreach (var att in byUser.Values.Where(a => !present.Contains(a.UserId) && a.OpenSegmentStart is not null))
            {
                FlushAttendance(att, now, Factor(att.UserId, att.OpenSegmentStart ?? now));
                att.OpenSegmentStart = null;

                // Drop a drive-by: a member who left having accrued less than the guild's minimum.
                if (minTracked > 0 && TotalSeconds(att) < minTracked)
                {
                    db.VoiceAttendance.Remove(att);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static int TotalSeconds(VoiceAttendance a) => a.TotalMinutes * 60 + a.CarrySeconds;

    /// <summary>
    /// Close active sessions that have run past their guild's <c>MaxSessionHours</c> — a safety net so a session
    /// that's never stopped (forgotten manual op, deleted channel, bot kicked) can't accrue forever. Closing
    /// finalizes attendance and awards as normal. Returns how many were closed.
    /// </summary>
    public async Task<int> CloseStaleSessionsAsync(DateTimeOffset? at = null, CancellationToken ct = default)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var active = await db.ListAllActiveSessionsAsync(ct);
        if (active.Count == 0)
        {
            return 0;
        }

        var maxHoursByGuild = new Dictionary<ulong, int>();
        var closed = 0;

        foreach (var session in active)
        {
            if (!maxHoursByGuild.TryGetValue(session.GuildId, out var maxHours))
            {
                maxHours = (await db.GetSettingsAsync(session.GuildId, ct)).MaxSessionHours;
                maxHoursByGuild[session.GuildId] = maxHours;
            }

            if (maxHours > 0 && now - session.StartedAt >= TimeSpan.FromHours(maxHours))
            {
                await CloseAsync(session.Id, now, ct: ct);
                closed++;
            }
        }

        return closed;
    }

    /// <summary>
    /// A member's one-time opt-out from a single active session: record the opt-out (so the reconcile skips them
    /// for the rest of the session) and remove their current attendance row. Returns false if the session isn't
    /// an active session of the guild.
    /// </summary>
    public async Task<bool> OptOutMemberAsync(ulong guildId, Guid sessionId, ulong userId, CancellationToken ct = default)
    {
        if (!await db.IsActiveSessionAsync(guildId, sessionId, ct))
        {
            return false;
        }

        if (!await db.HasSessionOptOutAsync(sessionId, userId, ct))
        {
            db.SessionOptOuts.Add(new SessionOptOut { SessionId = sessionId, UserId = userId });
        }

        var attendance = await db.FindAttendanceAsync(sessionId, userId, ct);
        if (attendance is not null)
        {
            db.VoiceAttendance.Remove(attendance);
        }

        await db.SaveChangesAsync(ct);
        return true;
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

        // Reward multipliers: weight the final flush + (optionally) the presence bonuses.
        var settings = await db.GetSettingsAsync(session.GuildId, ct);
        var attendeeIds = session.Attendance.Select(a => a.UserId).ToList();
        var mult = await multipliers.LoadAsync(session.GuildId, ct);
        var rolesByUser = mult.IsEmpty ? [] : await db.RoleIdsByUserAsync(session.GuildId, attendeeIds, ct);
        decimal SessionFactor(ulong userId, DateTimeOffset at) =>
            mult.IsEmpty ? 1m : mult.Factor(MultiplierScope.Sessions, at, rolesByUser.GetValueOrDefault(userId));

        foreach (var attendance in session.Attendance)
        {
            FlushAttendance(attendance, now, SessionFactor(attendance.UserId, attendance.OpenSegmentStart ?? now));
            attendance.OpenSegmentStart = null;
        }

        // Drop drive-bys (members who never accrued the guild's minimum) so they're neither counted nor rewarded.
        var minTracked = settings.MinTrackedSeconds;
        if (minTracked > 0)
        {
            var driveBys = session.Attendance.Where(a => TotalSeconds(a) < minTracked).ToList();
            foreach (var d in driveBys)
            {
                db.VoiceAttendance.Remove(d);
                session.Attendance.Remove(d);
            }
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

            // Reward is computed from the multiplier-weighted minutes (falls back to raw for legacy/zero rows).
            var weightedMinutes = attendance.WeightedSeconds > 0m
                ? (int)(attendance.WeightedSeconds / 60m)
                : attendance.TotalMinutes;

            if (weightedMinutes > 0)
            {
                await awards.AwardPointsAsync(
                    session.GuildId, attendance.UserId, weightedMinutes * rate,
                    CurrencyLedgerSource.TrackingSession, $"session:{sessionId}:user:{attendance.UserId}",
                    "Voice attendance", ct);

                // Sessions (only) also mint the guild's chosen spendable currency, by minutes / minutes-per-coin.
                if (coinCurrency is not null)
                {
                    var coins = weightedMinutes / minutesPerCoin;
                    if (coins > 0)
                    {
                        await awards.AwardAsync(
                            session.GuildId, attendance.UserId, coinCurrency.Id, coins,
                            CurrencyLedgerSource.TrackingSession, $"session:{sessionId}:user:{attendance.UserId}:coin",
                            "Session participation", ct);
                    }
                }
            }

            await AwardPresenceBonusesAsync(session, attendance, settings, SessionFactor, ct);
        }

        return true;
    }

    /// <summary>Flat POINTS bonuses for being present at the session's start and/or end (windows configurable;
    /// optionally scaled by the multiplier active at that moment). Idempotent per-member source keys.</summary>
    private async Task AwardPresenceBonusesAsync(
        TrackingSession session, VoiceAttendance attendance, GuildSettings settings,
        Func<ulong, DateTimeOffset, decimal> factorAt, CancellationToken ct)
    {
        var startedAt = session.StartedAt;
        var endedAt = session.EndedAt ?? startedAt;

        if (settings.SessionStartBonus > 0
            && attendance.FirstJoinedAt <= startedAt.AddMinutes(settings.StartBonusWindowMinutes))
        {
            var amount = settings.MultiplyPresenceBonuses
                ? (int)Math.Floor(settings.SessionStartBonus * factorAt(attendance.UserId, startedAt))
                : settings.SessionStartBonus;
            if (amount > 0)
            {
                await awards.AwardPointsAsync(
                    session.GuildId, attendance.UserId, amount, CurrencyLedgerSource.TrackingSession,
                    $"session:{session.Id}:user:{attendance.UserId}:startbonus", "Session start bonus", ct);
            }
        }

        if (settings.SessionEndBonus > 0
            && attendance.LastLeftAt is { } left
            && left >= endedAt.AddMinutes(-settings.EndBonusWindowMinutes))
        {
            var amount = settings.MultiplyPresenceBonuses
                ? (int)Math.Floor(settings.SessionEndBonus * factorAt(attendance.UserId, endedAt))
                : settings.SessionEndBonus;
            if (amount > 0)
            {
                await awards.AwardPointsAsync(
                    session.GuildId, attendance.UserId, amount, CurrencyLedgerSource.TrackingSession,
                    $"session:{session.Id}:user:{attendance.UserId}:endbonus", "Session end bonus", ct);
            }
        }
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

    /// <summary>
    /// Upper bound on minutes credited in a single attendance flush. Generous (12h) so it never clips a normal
    /// session — the 5-minute sweep keeps real flushes small — but caps an absurd stale watermark from a gateway
    /// gap that <see cref="VoidOpenAttendanceAsync"/> (restart) and <c>MaxSessionHours</c> (total) don't catch.
    /// </summary>
    private const int MaxFlushMinutes = 12 * 60;

    /// <summary>Roll eligible elapsed time on the open segment into whole minutes (sub-minute remainder carries),
    /// then advance the watermark. No-op when no segment is open. Caller decides whether to keep the segment open
    /// (advanced) or close it (null the start). Restart staleness is handled by <see cref="VoidOpenAttendanceAsync"/>;
    /// the clamp is a final sanity bound for unobserved gateway gaps.</summary>
    private static void FlushAttendance(VoiceAttendance attendance, DateTimeOffset now, decimal factor = 1m)
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

        if (minutes > MaxFlushMinutes)
        {
            minutes = MaxFlushMinutes;
            attendance.CarrySeconds = 0;
        }

        attendance.TotalMinutes += minutes;
        // Multiplier-weighted basis: the whole minutes credited this flush, scaled by the factor active now.
        // When factor is 1 this equals minutes×60, so WeightedSeconds tracks raw seconds and reward is unchanged.
        attendance.WeightedSeconds += minutes * 60m * factor;
        attendance.LastLeftAt = now;
    }
}
