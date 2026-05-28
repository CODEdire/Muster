using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Entities.Members;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>A member's live voice presence in a channel, as seen on the gateway roster at reconcile time.
/// Muted (can't speak) and deafened (can't hear — checked out) are separate so guards can treat them differently.</summary>
public readonly record struct VoiceMemberSnapshot(ulong UserId, bool IsBot, bool IsMuted, bool IsDeafened);

/// <summary>
/// The always-on "background" plane. Two accruals run per monitored voice channel against the live voice
/// roster (snapshot-driven + idempotent, so safe on every event and on the periodic sweep):
/// <list type="bullet">
/// <item><b>Active time</b> — raw presence minutes, unguarded, overlaps everything (even bounded Sessions).
/// Stats only: rolls into <see cref="DailyActivityRollup.VoiceMinutes"/> and a per-season counter.</item>
/// <item><b>Reward time</b> — guarded (anti-AFK) and suppressed while a <see cref="TrackingSession"/> owns the
/// channel. Mints POINTS (Reward-mode channels only).</item>
/// </list>
/// Both honor the member's tracking consent (background lane); an opted-out member accrues neither.
/// </summary>
public class BackgroundTrackingService(MusterDbContext db, ICurrencyService awards, GuildAuthorizationService auth, RewardMultiplierService multipliers)
{
    /// <summary>
    /// Upper bound on minutes credited in a single flush. Reconciles happen on every voice event and on a
    /// ~5-minute sweep, so a segment never legitimately accrues much more than the sweep window between
    /// flushes. A larger flush means an unobserved gap (downtime / gateway reconnect with a stale watermark),
    /// so we credit at most one window and drop the excess — downtime must not over-award or over-count.
    /// </summary>
    private const int MaxFlushMinutes = 10;

    /// <summary>
    /// Clear every open accrual segment (reward + active) — used once on bot startup. After a restart a
    /// watermark can be hours stale and we didn't observe presence while disconnected; the next reconcile
    /// reopens segments fresh against the live roster. Returns how many rows were touched.
    /// </summary>
    public async Task<int> VoidOpenSegmentsAsync(CancellationToken ct = default)
    {
        var open = await db.ListOpenBackgroundPresencesAsync(ct);
        foreach (var p in open)
        {
            p.OpenSegmentStart = null;
            p.CarrySeconds = 0;
            p.ActiveOpenSegmentStart = null;
            p.ActiveCarrySeconds = 0;
            p.PresentSince = null;
        }

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return open.Count;
    }

    /// <summary>
    /// Reconcile every monitored voice channel in a guild against the current roster. <paramref name="occupantsByChannel"/>
    /// maps channel id → the members currently in it; a channel absent from the map is treated as empty.
    /// </summary>
    public async Task ReconcileGuildAsync(
        ulong guildId,
        IReadOnlyDictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> occupantsByChannel,
        DateTimeOffset? at = null,
        CancellationToken ct = default)
    {
        var channels = await db.ListVoiceTrackedChannelsAsync(guildId, ct);
        if (channels.Count == 0)
        {
            return;
        }

        var now = at ?? DateTimeOffset.UtcNow;
        var settings = await db.GetSettingsAsync(guildId, ct);
        var seasonId = await db.ActiveSeasonIdAsync(guildId, ct);

        var trackedChannelIds = channels.Select(c => c.ChannelId).ToHashSet();
        var presentUserIds = occupantsByChannel
            .Where(kv => trackedChannelIds.Contains(kv.Key))
            .SelectMany(kv => kv.Value)
            .Where(m => !m.IsBot)
            .Select(m => m.UserId)
            .Distinct()
            .ToList();
        var choices = await db.TrackingChoicesAsync(guildId, presentUserIds, ct);

        // Reward multipliers: load once per pass; resolve per-member role factors from a single roles query.
        var mult = await multipliers.LoadAsync(guildId, ct);
        var rolesByUser = mult.IsEmpty ? [] : await db.RoleIdsByUserAsync(guildId, presentUserIds, ct);

        var staged = false;
        foreach (var cfg in channels)
        {
            var occupants = occupantsByChannel.TryGetValue(cfg.ChannelId, out var list) ? list : [];
            staged |= await ReconcileChannelAsync(cfg, occupants, now, settings.BackgroundTrackingOptIn, choices, seasonId, mult, rolesByUser, ct);
        }

        if (staged)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Returns true if any presence row was touched (so the caller knows to commit).</summary>
    private async Task<bool> ReconcileChannelAsync(
        GuildChannel cfg, IReadOnlyList<VoiceMemberSnapshot> occupants, DateTimeOffset now,
        bool guildBackgroundOptIn, IReadOnlyDictionary<ulong, TrackingChoice> choices, Guid? seasonId,
        MultiplierSet mult, IReadOnlyDictionary<ulong, List<ulong>> rolesByUser, CancellationToken ct)
    {
        var presences = await db.ListPresencesForChannelAsync(cfg.GuildId, cfg.ChannelId, ct);
        var byUser = presences.ToDictionary(p => p.UserId);

        var humans = occupants.Where(o => !o.IsBot).ToList();
        var present = humans.Select(h => h.UserId).ToHashSet();
        var sessionActive = await db.HasActiveSessionForChannelAsync(cfg.GuildId, cfg.ChannelId, ct);
        var isReward = cfg.Mode == TrackedChannelMode.Reward;
        var touched = false;

        // Channel-wide multiplier context: occupants now + how long the channel has been continuously occupied
        // (resets when it empties). Drives the min-people / min-time bonus conditions for everyone in the channel.
        if (humans.Count > 0)
        {
            if (cfg.OccupiedSince is null)
            {
                cfg.OccupiedSince = now;
                touched = true;
            }
        }
        else if (cfg.OccupiedSince is not null)
        {
            cfg.OccupiedSince = null;
            touched = true;
        }

        var channelMinutes = cfg.OccupiedSince is { } since ? Math.Max(0, (int)(now - since).TotalMinutes) : 0;
        var context = new MultiplierContext(humans.Count, channelMinutes);

        foreach (var member in humans)
        {
            var consent = TrackingConsentResolver.Resolve(choices.GetValueOrDefault(member.UserId), guildBackgroundOptIn);

            if (!consent.Background)
            {
                // Opted out of the background plane: stop and clear any open accrual (don't credit the future).
                if (byUser.TryGetValue(member.UserId, out var optedOut) && HasOpenSegment(optedOut))
                {
                    ClearSegments(optedOut);
                    touched = true;
                }

                continue;
            }

            var p = GetOrCreate(byUser, cfg, member.UserId, now);
            touched = true;

            // Active time: raw presence, unguarded, overlaps everything.
            p.PresentSince ??= now; // true "here since" for this stint — set on join, never advanced on flush
            if (p.ActiveOpenSegmentStart is null)
            {
                p.ActiveOpenSegmentStart = now;
            }
            else
            {
                await FlushActiveAsync(p, now, seasonId, ct);
            }

            // Reward time: guarded + suppressed while a Session owns the channel.
            var eligible = isReward && !sessionActive
                && cfg.Guards.Allows(member.IsMuted, member.IsDeafened, humans.Count);

            if (eligible)
            {
                if (p.OpenSegmentStart is null)
                {
                    p.OpenSegmentStart = now;
                }
                else
                {
                    await FlushRewardAsync(cfg, p, now, mult, rolesByUser, context, ct);
                }
            }
            else if (p.OpenSegmentStart is not null)
            {
                await FlushRewardAsync(cfg, p, now, mult, rolesByUser, context, ct);
                p.OpenSegmentStart = null;
            }
        }

        // Members who have left the channel: settle and close both lanes.
        foreach (var p in presences.Where(p => !present.Contains(p.UserId)))
        {
            var any = false;

            if (p.ActiveOpenSegmentStart is not null)
            {
                await FlushActiveAsync(p, now, seasonId, ct);
                p.ActiveOpenSegmentStart = null;
                p.PresentSince = null; // left the channel — end this stint
                any = true;
            }

            if (p.OpenSegmentStart is not null)
            {
                await FlushRewardAsync(cfg, p, now, mult, rolesByUser, context, ct);
                p.OpenSegmentStart = null;
                any = true;
            }

            touched |= any;
        }

        return touched;
    }

    private static bool HasOpenSegment(BackgroundVoicePresence p)
        => p.OpenSegmentStart is not null || p.ActiveOpenSegmentStart is not null;

    private static void ClearSegments(BackgroundVoicePresence p)
    {
        p.OpenSegmentStart = null;
        p.CarrySeconds = 0;
        p.ActiveOpenSegmentStart = null;
        p.ActiveCarrySeconds = 0;
        p.PresentSince = null;
    }

    private BackgroundVoicePresence GetOrCreate(
        Dictionary<ulong, BackgroundVoicePresence> byUser, GuildChannel cfg, ulong userId, DateTimeOffset now)
    {
        if (byUser.TryGetValue(userId, out var existing))
        {
            return existing;
        }

        var created = new BackgroundVoicePresence
        {
            Id = Guid.NewGuid(),
            GuildId = cfg.GuildId,
            UserId = userId,
            ChannelId = cfg.ChannelId,
            AwardedDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        db.BackgroundVoicePresences.Add(created);
        byUser[userId] = created;
        return created;
    }

    /// <summary>Roll active presence minutes into the daily rollup + season counter (stats only; no guards, no pay).</summary>
    private async Task FlushActiveAsync(BackgroundVoicePresence p, DateTimeOffset now, Guid? seasonId, CancellationToken ct)
    {
        if (p.ActiveOpenSegmentStart is not { } start)
        {
            return;
        }

        var elapsed = (int)(now - start).TotalSeconds;
        var totalSeconds = p.ActiveCarrySeconds + Math.Max(0, elapsed);
        var minutes = totalSeconds / 60;
        p.ActiveCarrySeconds = totalSeconds % 60;
        p.ActiveOpenSegmentStart = now;

        if (minutes > MaxFlushMinutes)
        {
            minutes = MaxFlushMinutes;
            p.ActiveCarrySeconds = 0;
        }

        if (minutes <= 0)
        {
            return;
        }

        var date = DateOnly.FromDateTime(now.UtcDateTime);
        var rollup = await db.FindDailyRollupAsync(p.GuildId, p.UserId, p.ChannelId, date, ct);
        if (rollup is null)
        {
            rollup = new DailyActivityRollup { GuildId = p.GuildId, UserId = p.UserId, ChannelId = p.ChannelId, Date = date };
            db.DailyActivityRollups.Add(rollup);
        }

        rollup.VoiceMinutes += minutes;

        if (seasonId is { } sid)
        {
            var sp = await db.FindSeasonParticipationAsync(p.GuildId, p.UserId, sid, ct);
            if (sp is null)
            {
                sp = new SeasonParticipation { Id = Guid.NewGuid(), GuildId = p.GuildId, UserId = p.UserId, SeasonId = sid };
                db.SeasonParticipations.Add(sp);
            }

            sp.VoiceMinutes += minutes;
        }
    }

    /// <summary>
    /// Convert eligible elapsed reward time into a POINTS award (whole minutes; remainder carries), then advance
    /// the watermark. Idempotent on the segment-start source key, daily-capped, gated to participants.
    /// </summary>
    private async Task FlushRewardAsync(GuildChannel cfg, BackgroundVoicePresence p, DateTimeOffset now,
        MultiplierSet mult, IReadOnlyDictionary<ulong, List<ulong>> rolesByUser, MultiplierContext context, CancellationToken ct)
    {
        if (p.OpenSegmentStart is not { } start)
        {
            return;
        }

        var elapsed = (int)(now - start).TotalSeconds;
        var totalSeconds = p.CarrySeconds + Math.Max(0, elapsed);
        var minutes = totalSeconds / 60;
        p.CarrySeconds = totalSeconds % 60;
        p.OpenSegmentStart = now; // advance the watermark even when no whole minute landed

        if (minutes > MaxFlushMinutes)
        {
            // A flush larger than one sweep window is an unobserved gap (downtime / reconnect), not presence
            // we tracked — credit at most one window and drop the excess so a stale watermark can't over-award.
            minutes = MaxFlushMinutes;
            p.CarrySeconds = 0;
        }

        if (minutes <= 0 || cfg.PointsPerMinute <= 0)
        {
            return;
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (p.AwardedDate != today)
        {
            p.AwardedDate = today;
            p.AwardedPointsToday = 0;
        }

        // Attribute the segment to the regime in force when it started (boundary flushes keep a segment within
        // one regime, so the start-time factor is exact for the whole segment).
        var factor = mult.IsEmpty ? 1m : mult.Factor(MultiplierScope.BackgroundVoice, start, rolesByUser.GetValueOrDefault(p.UserId), context);
        var points = (int)Math.Floor(minutes * cfg.PointsPerMinute * factor);
        if (points <= 0)
        {
            return; // a sub-1 multiplier can floor the award to nothing
        }

        if (cfg.DailyCapPoints > 0)
        {
            var remaining = cfg.DailyCapPoints - p.AwardedPointsToday;
            if (remaining <= 0)
            {
                return;
            }

            points = Math.Min(points, remaining);
        }

        if (!await auth.IsParticipantAsync(p.GuildId, p.UserId, ct))
        {
            return;
        }

        var sourceId = $"bg:{p.ChannelId}:{p.UserId}:{start.UtcDateTime:yyyyMMddHHmmss}";
        await awards.StagePointsAsync(
            p.GuildId, p.UserId, points, CurrencyLedgerSource.Background, sourceId, "Background voice presence", ct);
        p.AwardedPointsToday += points;
    }
}
