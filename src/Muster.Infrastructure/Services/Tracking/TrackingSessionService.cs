using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Musters;
using Wolverine;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>
/// Channel-activity tracking sessions. While a session is active, members' voice presence in its
/// channel accumulates; closing the session awards points proportional to minutes attended. Sessions
/// are opened manually by an admin or bound to a Discord scheduled event.
/// </summary>
public class TrackingSessionService(MusterDbContext db, ICurrencyService awards, GuildAuthorizationService auth, RewardMultiplierService multipliers, IMessageBus bus, MusterService? musters = null)
{
    public const int DefaultPointsPerMinute = 1;

    /// <summary>
    /// Open a manual session. Anti-AFK guards default to <paramref name="requireUnmuted"/>/<paramref name="requireNotAlone"/>
    /// when supplied; a null falls back to the guild's <c>ApplyAfkGuardsToSessions</c> policy.
    /// <paramref name="createMuster"/> null falls back to the guild's <c>AutoCreateMusterOnSession</c> default.
    /// </summary>
    public async Task<TrackingSession> OpenManualAsync(
        ulong guildId, ulong voiceChannelId, ulong openedBy,
        string? name = null, string? channelName = null,
        bool? requireUnmuted = null, bool? requireUndeafened = null, bool? requireNotAlone = null,
        bool? createMuster = null, CancellationToken ct = default)
        => await OpenAsync(
            guildId, voiceChannelId, channelName, TrackingSessionSource.Manual, scheduledEventId: null, openedBy,
            name ?? "Manual session", requireUnmuted, requireUndeafened, requireNotAlone, createMuster, ct);

    public async Task<TrackingSession> OpenForScheduledEventAsync(
        ulong guildId, ulong voiceChannelId, ulong scheduledEventId, string? name = null, string? channelName = null,
        bool? createMuster = null, CancellationToken ct = default)
        => await OpenAsync(
            guildId, voiceChannelId, channelName, TrackingSessionSource.DiscordScheduledEvent, scheduledEventId, openedBy: 0,
            name ?? "Scheduled event", requireUnmuted: null, requireUndeafened: null, requireNotAlone: null, createMuster, ct);

    /// <summary>Open a session bound to a scheduled event, unless one is already active for it.</summary>
    public async Task<TrackingSession?> EnsureForScheduledEventAsync(
        ulong guildId, ulong voiceChannelId, ulong scheduledEventId, string? name = null, string? channelName = null, CancellationToken ct = default)
    {
        var alreadyOpen = await db.HasActiveSessionForEventAsync(guildId, scheduledEventId, ct);
        if (alreadyOpen)
        {
            return null;
        }

        return await OpenForScheduledEventAsync(guildId, voiceChannelId, scheduledEventId, name, channelName, ct: ct);
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

    /// <summary>
    /// Safety net for a missed/unhandled "event ended" gateway update: close any active session bound to a
    /// scheduled event that is no longer running. <paramref name="activeScheduledEventIds"/> are the events still
    /// Active per the gateway; a bound session whose event isn't in that set is orphaned and gets closed. Returns
    /// how many were closed. Called from the periodic reconcile, so a stuck session recovers within a sweep.
    /// </summary>
    public async Task<int> CloseEndedScheduledEventSessionsAsync(
        ulong guildId, IReadOnlyCollection<ulong> activeScheduledEventIds, CancellationToken ct = default)
    {
        var bound = await db.ListActiveScheduledEventSessionsAsync(guildId, ct);
        var closed = 0;
        foreach (var session in bound)
        {
            if (session.ScheduledEventId is { } eventId && !activeScheduledEventIds.Contains(eventId))
            {
                await CloseAsync(session.Id, ct: ct);
                closed++;
            }
        }

        return closed;
    }

    private async Task<TrackingSession> OpenAsync(
        ulong guildId, ulong voiceChannelId, string? channelName, TrackingSessionSource source, ulong? scheduledEventId,
        ulong openedBy, string name, bool? requireUnmuted, bool? requireUndeafened, bool? requireNotAlone,
        bool? createMuster, CancellationToken ct)
    {
        // A null guard defaults to the guild's session-guard policy (so scheduled events follow it). Deafened
        // (checked out) and alone are the default AFK signals; merely muted (present, can't speak) is opt-in.
        var settings = await db.GetSettingsAsync(guildId, ct);
        var applyGuards = settings.ApplyAfkGuardsToSessions;
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

        // Tell live session lists a new session exists (an empty channel produces no reconcile change to ping on).
        await bus.PublishAsync(new SessionAttendanceChanged(guildId, session.Id, SessionChangeKind.Opened));

        // Optionally spin up a check-in muster for the session (guild default, overridable per open). It posts to the
        // configured muster channel, links to the session, and gates the session coin on check-in (mode Any).
        if ((createMuster ?? settings.AutoCreateMusterOnSession) && musters is not null)
        {
            var muster = await musters.CreatePointsAsync(
                guildId, settings.Musters.MusterChannelId, title: null, prompt: $"Check in for {name}",
                rewardPoints: 0, capacity: null, expiresAt: null, createdBy: openedBy, sessionId: session.Id, ct: ct);

            session.CoinGate = SessionCoinGate.Any;
            await db.SaveChangesAsync(ct);

            await bus.PublishAsync(new MusterChanged(guildId, muster.Id, MusterChangeKind.Created));
        }

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
        // ctx carries the session's channel-wide bonus conditions (occupants + minutes since it started).
        decimal Factor(ulong userId, DateTimeOffset at, MultiplierContext ctx)
            => mult.IsEmpty ? 1m : mult.Factor(MultiplierScope.Sessions, at, rolesByUser.GetValueOrDefault(userId), ctx);

        // Sessions whose visible roster/status changed this pass — a live watcher would see the difference.
        // We re-fetch nothing here; we just note which sessions to ping so the web view re-reads + re-renders.
        var changedSessions = new List<Guid>();

        foreach (var session in sessions)
        {
            var changed = false;
            var optedOut = await db.OptedOutUserIdsAsync(session.Id, ct);
            var occupants = occupantsByChannel.TryGetValue(session.VoiceChannelId, out var list) ? list : [];
            var humans = occupants
                .Where(o => !o.IsBot && choices.GetValueOrDefault(o.UserId) != TrackingChoice.AllOut && !optedOut.Contains(o.UserId))
                .ToList();
            var present = humans.Select(h => h.UserId).ToHashSet();
            var byUser = (await db.AttendanceForSessionAsync(session.Id, ct)).ToDictionary(a => a.UserId);

            // Channel-wide bonus context for this session: occupants now + minutes the session has run.
            var sctx = new MultiplierContext(humans.Count, Math.Max(0, (int)(now - session.StartedAt).TotalMinutes));

            foreach (var member in humans)
            {
                var eligible = session.Guards.Allows(member.IsMuted, member.IsDeafened, humans.Count);
                var att = GetOrCreateAttendance(session.Id, byUser, member.UserId, now);
                att.LastSeenAt = now; // present in the channel (eligible or not)

                if (!att.InChannel)
                {
                    att.InChannel = true; // entered (brand new, or re-joined after leaving)
                    AddPresenceEvent(session.Id, member.UserId, SessionPresenceKind.Join, null, now);
                    changed = true;
                }

                if (eligible)
                {
                    if (att.OpenSegmentStart is null)
                    {
                        att.OpenSegmentStart = now; // resumed earning (paused -> active)
                        AddPresenceEvent(session.Id, member.UserId, SessionPresenceKind.Resume, null, now);
                        changed = true;
                    }
                    else
                    {
                        FlushAttendance(att, now, Factor(member.UserId, att.OpenSegmentStart ?? now, sctx));
                    }
                }
                else if (att.OpenSegmentStart is not null)
                {
                    FlushAttendance(att, now, Factor(member.UserId, att.OpenSegmentStart ?? now, sctx));
                    att.OpenSegmentStart = null; // paused (active -> not earning)
                    AddPresenceEvent(session.Id, member.UserId, SessionPresenceKind.Pause, PauseReason(session.Guards, member, humans.Count), now);
                    changed = true;
                }
            }

            // Anyone in the channel last pass but gone now has left (whether they were earning or paused).
            foreach (var att in byUser.Values.Where(a => a.InChannel && !present.Contains(a.UserId)))
            {
                if (att.OpenSegmentStart is not null)
                {
                    FlushAttendance(att, now, Factor(att.UserId, att.OpenSegmentStart ?? now, sctx));
                    att.OpenSegmentStart = null;
                }

                att.InChannel = false;
                AddPresenceEvent(session.Id, att.UserId, SessionPresenceKind.Leave, null, now);
                changed = true; // left the channel

                // Drop a drive-by: a member who left having accrued less than the guild's minimum. Their presence
                // events are harmless if left (the timeline filters to members with an attendance row).
                if (minTracked > 0 && TotalSeconds(att) < minTracked)
                {
                    db.VoiceAttendance.Remove(att);
                }
            }

            if (changed)
            {
                changedSessions.Add(session.Id);
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var sessionId in changedSessions)
        {
            await bus.PublishAsync(new SessionAttendanceChanged(guildId, sessionId, SessionChangeKind.Roster));
        }
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
        await bus.PublishAsync(new SessionAttendanceChanged(guildId, sessionId, SessionChangeKind.OptOut));
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

    /// <summary>Append a presence transition to the session's event stream (drives the timeline charts + audit log).</summary>
    private void AddPresenceEvent(Guid sessionId, ulong userId, SessionPresenceKind kind, string? reason, DateTimeOffset at)
        => db.SessionPresenceEvents.Add(new SessionPresenceEvent
        {
            SessionId = sessionId,
            UserId = userId,
            Kind = kind,
            Reason = reason,
            AtUtc = at,
        });

    /// <summary>The tripped anti-AFK guard(s) that paused a member, as a short label for the audit log; null if none.</summary>
    private static string? PauseReason(AfkGuards guards, VoiceMemberSnapshot member, int humanCount)
    {
        var parts = new List<string>(3);
        if (guards.Unmuted() && member.IsMuted) { parts.Add("muted"); }
        if (guards.Undeafened() && member.IsDeafened) { parts.Add("deafened"); }
        if (guards.NotAlone() && humanCount < 2) { parts.Add("alone"); }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
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
        // Channel-wide bonus context at close: total attendees + the session's run length.
        var cctx = new MultiplierContext(session.Attendance.Count, Math.Max(0, (int)(now - session.StartedAt).TotalMinutes));
        decimal SessionFactor(ulong userId, DateTimeOffset at) =>
            mult.IsEmpty ? 1m : mult.Factor(MultiplierScope.Sessions, at, rolesByUser.GetValueOrDefault(userId), cctx);

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

        // The session now reads as Ended — tell watchers before the (slower) award pass runs.
        await bus.PublishAsync(new SessionAttendanceChanged(session.GuildId, sessionId, SessionChangeKind.Closed));

        var rate = pointsPerMinute ?? await ResolvePointsPerMinuteAsync(session.GuildId, ct);
        var (coinCurrency, minutesPerCoin) = await ResolveSessionCoinAsync(session.GuildId, ct);

        // Linked musters drive three things at close: coin gating, per-muster bonus rewards, and auto-close.
        var linkedMusters = await db.LinkedMustersForSessionAsync(sessionId, ct);

        // Coin gating: when this session has a gate mode and at least one linked muster, only attendees on the
        // qualifying roster earn the spendable coin (points are never gated). Every assigned muster counts — under
        // All it is genuinely required, so a muster nobody checked into legitimately blocks the coin (don't assign it,
        // or use Any, if that's not intended). No linked muster at all → null = a good completion mints coin to all.
        // Any = union of rosters; All = intersection.
        HashSet<ulong>? coinEligible = null;
        if (coinCurrency is not null && session.CoinGate != SessionCoinGate.None && linkedMusters.Count > 0)
        {
            coinEligible = session.CoinGate == SessionCoinGate.All
                ? linkedMusters.Select(m => new HashSet<ulong>(m.Roster)).Aggregate((acc, next) => { acc.IntersectWith(next); return acc; })
                : linkedMusters.SelectMany(m => m.Roster).ToHashSet();
        }

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

                // Sessions (only) also mint the guild's chosen spendable currency, by minutes / minutes-per-coin —
                // gated on muster check-in when the session has a gate (coinEligible null = no gate, mint to all).
                if (coinCurrency is not null && (coinEligible is null || coinEligible.Contains(attendance.UserId)))
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

            // Per-muster bonus: a linked muster's own reward pays here (deferred from check-in), to attendees who
            // both checked in AND showed up — "complete together". Idempotent per (muster, user), so a muster linked
            // to several sessions pays its bonus once (the first attended close).
            foreach (var m in linkedMusters)
            {
                if (m.Status != MusterStatus.Cancelled && m.Reward > 0 && m.Roster.Contains(attendance.UserId))
                {
                    await awards.AwardAsync(
                        session.GuildId, attendance.UserId, m.CurrencyId, m.Reward,
                        CurrencyLedgerSource.Muster, $"muster:{m.Id}:user:{attendance.UserId}",
                        $"Muster check-in: {m.Prompt}", ct);
                }
            }
        }

        // Auto-close linked musters now that the session has ended. A muster spanning several sessions only closes
        // once all of them are closed (this session is already marked Closed above), so a multi-session muster
        // survives until its last session ends.
        var closedIds = new List<Guid>();
        foreach (var m in linkedMusters.Where(m => m.Status == MusterStatus.Open))
        {
            var stillActive = await db.MusterSessionLinks
                .Where(l => l.MusterId == m.Id)
                .Join(db.TrackingSessions, l => l.SessionId, s => s.Id, (l, s) => s.Status)
                .AnyAsync(st => st == TrackingSessionStatus.Active, ct);
            if (stillActive)
            {
                continue; // another linked session is still running — keep the muster open
            }

            var entity = await db.ReactionMusters.FirstOrDefaultAsync(x => x.Id == m.Id, ct);
            if (entity is { Status: MusterStatus.Open })
            {
                entity.Status = MusterStatus.Closed;
                entity.ClosedAt = now;
                closedIds.Add(m.Id);
            }
        }

        if (closedIds.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var id in closedIds)
            {
                await bus.PublishAsync(new MusterChanged(session.GuildId, id, MusterChangeKind.Closed));
            }
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
