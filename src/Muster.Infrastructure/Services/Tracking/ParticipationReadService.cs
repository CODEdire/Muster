using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>A member's rank in the voice-participation leaderboard.</summary>
public record VoiceLeaderboardEntry(ulong UserId, int VoiceMinutes);

/// <summary>A leaderboard row enriched for display: global <see cref="Rank"/>, name + avatar, minutes, and a
/// <see cref="BarPercent"/> (0–100) relative to the #1 member so bars stay consistent across pages.</summary>
public record VoiceLeaderboardRow(int Rank, ulong UserId, string Name, string? AvatarUrl, int VoiceMinutes, int BarPercent);

/// <summary>A member's standing on the voice leaderboard. <see cref="Rank"/> is 1-based; 0 = unranked (no minutes).</summary>
public record VoiceRank(int Minutes, int Rank);

/// <summary>An in-progress tracking session (live ops view).</summary>
public record ActiveSessionView(
    Guid Id, string Name, TrackingSessionSource Source, ulong VoiceChannelId, string VoiceChannelName, ulong? ScheduledEventId,
    DateTimeOffset StartedAt, int Attendees, int PresentNow);

/// <summary>A finished tracking session (history view).</summary>
public record RecentSessionView(
    Guid Id, string Name, TrackingSessionSource Source, ulong VoiceChannelId, string VoiceChannelName,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int Attendees, int TotalMinutes);

/// <summary>Which sessions the unified grid shows.</summary>
public enum SessionStatusFilter { Active, Ended, All }

/// <summary>One row of the unified sessions datagrid — active and ended in a single shape (live ops + history).
/// <see cref="InVoice"/> is the honest "in the channel now" count (present, earning or paused); <see cref="PresentNow"/>
/// is the narrower "earning now" count.</summary>
public record SessionGridRow(
    Guid Id, string Name, TrackingSessionSource Source, ulong VoiceChannelId, string VoiceChannelName,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool Active, int Attendees, int PresentNow, int InVoice, int TotalMinutes);

/// <summary>One member present in a monitored channel right now (live Background tab). <see cref="Earning"/> =
/// currently accruing reward (eligible + a Reward channel); else present-only (paused, or a Stats channel).
/// <see cref="MinutesToday"/> is their cumulative active voice minutes in this channel today; <see cref="PresentSince"/>
/// is when they joined for the current stint (their live "here since").</summary>
public record BackgroundMemberRow(
    ulong UserId, string Name, string? AvatarUrl, bool Earning, int MinutesToday, int PointsToday, DateTimeOffset? PresentSince);

/// <summary>A monitored channel currently carrying background presence, with its mode/rate + present members.</summary>
public record BackgroundChannelView(
    ulong ChannelId, string ChannelName, TrackedChannelMode Mode, int PointsPerMinute, IReadOnlyList<BackgroundMemberRow> Members);

/// <summary>A member's own voice participation summary (self-view).</summary>
public record MemberVoiceStats(int SeasonMinutes, int AllTimeMinutes, int SeasonRank);

/// <summary>A session from the member's own perspective (their minutes), for the Me dashboard + personal history.</summary>
public record MemberSessionView(
    Guid SessionId, string Name, string VoiceChannelName, int MyMinutes, bool PresentNow, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>A page of grid rows plus the totals a pager needs.</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

/// <summary>One member's row within a session's attendance roster (drill-in detail). <see cref="RewardMinutes"/>
/// is the multiplier-weighted minutes that drive the payout (falls back to raw minutes for legacy rows).
/// <see cref="PresentNow"/> = earning right now; <see cref="InChannel"/> = in the voice channel right now
/// (earning or paused) — the authoritative present/paused/gone signal (no last-seen guessing).</summary>
public record SessionMemberRow(
    ulong UserId, string Name, string? AvatarUrl, DateTimeOffset FirstJoinedAt,
    int TotalMinutes, int RewardMinutes, bool PresentNow, bool InChannel, DateTimeOffset? LastSeenAt);

/// <summary>One presence transition in a session's audit/timeline stream, with the member's display name.</summary>
public record SessionPresenceEventRow(ulong UserId, string Name, SessionPresenceKind Kind, string? Reason, DateTimeOffset At);

/// <summary>A single session with its full attendance roster (serves active + closed). <see cref="PointsPerMinute"/>
/// is the guild's voice rate, for projecting per-member points. <see cref="Events"/> is the chronological presence
/// stream (empty for legacy sessions, or when not requested) — drives the exact timeline + audit log.</summary>
public record SessionDetailView(
    Guid Id, string Name, TrackingSessionSource Source, ulong VoiceChannelId, string VoiceChannelName, ulong? ScheduledEventId,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool Active, ulong OpenedBy, string? OpenedByName,
    AfkGuards Guards, int PointsPerMinute, IReadOnlyList<SessionMemberRow> Members, IReadOnlyList<SessionPresenceEventRow> Events);

/// <summary>One member's participation totals over a date range, broken down by reward source.</summary>
public record ParticipationRow(
    ulong UserId,
    int VoiceMinutes,
    int MessageCount,
    long SessionPoints,
    long BackgroundPoints,
    long EventPoints,
    long QuestPoints,
    long MusterPoints);

/// <summary>
/// Read-only participation reporting (stats, not money): voice-time leaderboards and a per-member report
/// of voice minutes, message counts, and points earned by source. Aggregates the active-time rollups, the
/// per-season counter, and the currency ledger; never writes.
/// </summary>
public class ParticipationReadService(MusterDbContext db)
{
    /// <summary>True when the guild has an active season open. UI uses this to decide whether to offer the
    /// season-vs-all-time toggle on leaderboards.</summary>
    public async Task<bool> HasActiveSeasonAsync(ulong guildId, CancellationToken ct = default)
        => await db.ActiveSeasonIdAsync(guildId, ct) is not null;

    /// <summary>True when the member is currently being tracked in any live session on this guild.</summary>
    public Task<bool> IsMemberPresentInLiveSessionAsync(ulong guildId, ulong userId, CancellationToken ct = default)
        => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            db.TrackingSessions
                .Where(s => s.GuildId == guildId && s.Status == TrackingSessionStatus.Active)
                .SelectMany(s => s.Attendance)
                .Where(a => a.UserId == userId && a.InChannel),
            ct);

    /// <summary>
    /// Top members by voice minutes. <paramref name="seasonal"/>: null = auto (active season if one is open,
    /// else all time); true = the active season (falls back to all time when none); false = all time.
    /// </summary>
    public async Task<IReadOnlyList<VoiceLeaderboardEntry>> VoiceLeaderboardAsync(
        ulong guildId, int top = 10, bool? seasonal = null, CancellationToken ct = default)
    {
        var seasonId = await db.ActiveSeasonIdAsync(guildId, ct);

        if (seasonId is { } sid && seasonal != false)
        {
            return await db.SeasonParticipations
                .Where(p => p.GuildId == guildId && p.SeasonId == sid && p.VoiceMinutes > 0)
                .OrderByDescending(p => p.VoiceMinutes)
                .Take(top)
                .Select(p => new VoiceLeaderboardEntry(p.UserId, p.VoiceMinutes))
                .ToListAsync(ct);
        }

        var totals = await db.DailyActivityRollups
            .Where(r => r.GuildId == guildId && r.VoiceMinutes > 0)
            .GroupBy(r => r.UserId)
            .Select(g => new { UserId = g.Key, Voice = g.Sum(x => x.VoiceMinutes) })
            .ToListAsync(ct);

        return totals
            .OrderByDescending(t => t.Voice)
            .Take(top)
            .Select(t => new VoiceLeaderboardEntry(t.UserId, t.Voice))
            .ToList();
    }

    /// <summary>The full voice ranking (userId + minutes, desc) for the basis — bounded by participants. Used to
    /// assign a stable global rank before name-filtering/paging in memory.</summary>
    private async Task<List<(ulong UserId, int Minutes)>> RankedVoiceAsync(ulong guildId, bool? seasonal, CancellationToken ct)
    {
        var seasonId = await db.ActiveSeasonIdAsync(guildId, ct);

        if (seasonId is { } sid && seasonal != false)
        {
            return (await db.SeasonParticipations
                    .Where(p => p.GuildId == guildId && p.SeasonId == sid && p.VoiceMinutes > 0)
                    .OrderByDescending(p => p.VoiceMinutes)
                    .Select(p => new { p.UserId, p.VoiceMinutes })
                    .ToListAsync(ct))
                .Select(x => (x.UserId, x.VoiceMinutes)).ToList();
        }

        var totals = await db.DailyActivityRollups
            .Where(r => r.GuildId == guildId && r.VoiceMinutes > 0)
            .GroupBy(r => r.UserId)
            .Select(g => new { UserId = g.Key, Voice = g.Sum(x => x.VoiceMinutes) })
            .ToListAsync(ct);

        return totals.OrderByDescending(t => t.Voice).Select(t => (t.UserId, t.Voice)).ToList();
    }

    /// <summary>
    /// The voice leaderboard as a name-searchable, paged grid. Each row keeps its <b>global</b> rank and a bar
    /// percentage relative to the #1 member, so ranks/bars are meaningful on any page and a name search shows a
    /// member's true standing. <paramref name="seasonal"/> follows <see cref="VoiceLeaderboardAsync"/>.
    /// </summary>
    public async Task<PagedResult<VoiceLeaderboardRow>> VoiceLeaderboardPageAsync(
        ulong guildId, bool? seasonal, string? search, int page, int pageSize = 25, CancellationToken ct = default)
    {
        var ranked = await RankedVoiceAsync(guildId, seasonal, ct);
        var top = ranked.Count > 0 ? ranked[0].Minutes : 0;
        var withRank = ranked.Select((x, i) => (Rank: i + 1, x.UserId, x.Minutes)).ToList();

        // Name search filters the ranked set (preserving each member's global rank).
        if (!string.IsNullOrWhiteSpace(search))
        {
            var names = await db.UserDisplayNameMapAsync(withRank.Select(r => r.UserId).ToList(), ct);
            withRank = withRank
                .Where(r => names.TryGetValue(r.UserId, out var n) && n.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var total = withRank.Count;
        var pageItems = withRank.Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToList();

        var ids = pageItems.Select(r => r.UserId).ToList();
        var users = (await db.Users
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.Username, u.GlobalName, u.AvatarHash })
                .ToListAsync(ct))
            .ToDictionary(u => u.Id);

        var rows = pageItems.Select(r =>
        {
            users.TryGetValue(r.UserId, out var u);
            var name = u?.GlobalName ?? u?.Username ?? $"User {r.UserId}";
            var avatar = u is null ? null : Discord.DiscordCdn.AvatarUrl(u.Id, u.AvatarHash);
            var pct = top > 0 ? (int)Math.Round(r.Minutes * 100.0 / top) : 0;
            return new VoiceLeaderboardRow(r.Rank, r.UserId, name, avatar, r.Minutes, pct);
        }).ToList();

        return new PagedResult<VoiceLeaderboardRow>(rows, page, pageSize, total);
    }

    /// <summary>A member's own standing on the voice leaderboard (minutes + 1-based rank) for the same basis as
    /// <see cref="VoiceLeaderboardAsync"/> — so it can be pinned even when they're outside the visible top N.</summary>
    public async Task<VoiceRank> MemberVoiceRankAsync(
        ulong guildId, ulong userId, bool? seasonal = null, CancellationToken ct = default)
    {
        var seasonId = await db.ActiveSeasonIdAsync(guildId, ct);

        if (seasonId is { } sid && seasonal != false)
        {
            var minutes = await db.SeasonParticipations
                .Where(p => p.GuildId == guildId && p.SeasonId == sid && p.UserId == userId)
                .Select(p => (int?)p.VoiceMinutes).FirstOrDefaultAsync(ct) ?? 0;
            if (minutes <= 0)
            {
                return new VoiceRank(0, 0);
            }

            var ahead = await db.SeasonParticipations
                .CountAsync(p => p.GuildId == guildId && p.SeasonId == sid && p.VoiceMinutes > minutes, ct);
            return new VoiceRank(minutes, ahead + 1);
        }

        var totals = await db.DailyActivityRollups
            .Where(r => r.GuildId == guildId && r.VoiceMinutes > 0)
            .GroupBy(r => r.UserId)
            .Select(g => new { UserId = g.Key, Voice = g.Sum(x => x.VoiceMinutes) })
            .ToListAsync(ct);

        var mine = totals.FirstOrDefault(t => t.UserId == userId)?.Voice ?? 0;
        return mine <= 0 ? new VoiceRank(0, 0) : new VoiceRank(mine, totals.Count(t => t.Voice > mine) + 1);
    }

    /// <summary>
    /// Per-member participation totals over <paramref name="from"/>..<paramref name="to"/> (inclusive UTC days):
    /// voice minutes + message counts from the activity rollups, and points earned by source from the ledger.
    /// Ordered by voice minutes desc. Backs the admin CSV export.
    /// </summary>
    public async Task<IReadOnlyList<ParticipationRow>> ReportAsync(
        ulong guildId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var activity = await db.DailyActivityRollups
            .Where(r => r.GuildId == guildId && r.Date >= from && r.Date <= to)
            .GroupBy(r => r.UserId)
            .Select(g => new { UserId = g.Key, Voice = g.Sum(x => x.VoiceMinutes), Messages = g.Sum(x => x.MessageCount) })
            .ToListAsync(ct);

        // Ledger is timestamped; include the whole "to" day.
        var fromAt = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toAt = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var points = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && e.Amount > 0 && e.OccurredAt >= fromAt && e.OccurredAt < toAt)
            .GroupBy(e => new { e.UserId, e.SourceType })
            .Select(g => new { g.Key.UserId, g.Key.SourceType, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var pointsByUser = points
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.SourceType, x => x.Total));

        var userIds = activity.Select(a => a.UserId).Union(pointsByUser.Keys).Distinct();

        long PointsFor(ulong userId, CurrencyLedgerSource source)
            => pointsByUser.TryGetValue(userId, out var bySource) && bySource.TryGetValue(source, out var v) ? v : 0;

        var activityByUser = activity.ToDictionary(a => a.UserId);

        return userIds
            .Select(uid =>
            {
                activityByUser.TryGetValue(uid, out var a);
                return new ParticipationRow(
                    uid,
                    a?.Voice ?? 0,
                    a?.Messages ?? 0,
                    PointsFor(uid, CurrencyLedgerSource.TrackingSession),
                    PointsFor(uid, CurrencyLedgerSource.Background),
                    PointsFor(uid, CurrencyLedgerSource.Event),
                    PointsFor(uid, CurrencyLedgerSource.Quest),
                    PointsFor(uid, CurrencyLedgerSource.Muster));
            })
            .OrderByDescending(r => r.VoiceMinutes)
            .ThenByDescending(r => r.MessageCount)
            .ToList();
    }

    /// <summary>
    /// In-progress sessions with attendee + currently-present counts — the live-ops view. This is the one
    /// "live" read; it's isolated here so a future SSE/SignalR feed can push updates without changing callers
    /// (the page polls today; later it subscribes and re-renders on a TrackingSession change).
    /// </summary>
    public async Task<IReadOnlyList<ActiveSessionView>> ActiveSessionsAsync(ulong guildId, CancellationToken ct = default)
        => await db.TrackingSessions
            .Where(s => s.GuildId == guildId && s.Status == TrackingSessionStatus.Active)
            .OrderBy(s => s.StartedAt)
            .Select(s => new ActiveSessionView(
                s.Id, s.Name, s.Source, s.VoiceChannelId, s.VoiceChannelName, s.ScheduledEventId, s.StartedAt,
                s.Attendance.Count,
                s.Attendance.Count(a => a.OpenSegmentStart != null)))
            .ToListAsync(ct);

    /// <summary>Most recently ended sessions — the history view.</summary>
    public async Task<IReadOnlyList<RecentSessionView>> RecentSessionsAsync(ulong guildId, int take = 25, CancellationToken ct = default)
        => await db.TrackingSessions
            .Where(s => s.GuildId == guildId && s.Status == TrackingSessionStatus.Closed)
            .OrderByDescending(s => s.EndedAt)
            .Take(take)
            .Select(s => new RecentSessionView(
                s.Id, s.Name, s.Source, s.VoiceChannelId, s.VoiceChannelName, s.StartedAt, s.EndedAt,
                s.Attendance.Count,
                s.Attendance.Sum(a => a.TotalMinutes)))
            .ToListAsync(ct);

    /// <summary>A member's own voice participation: season minutes + all-time minutes + season rank.</summary>
    public async Task<MemberVoiceStats> MemberVoiceStatsAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var allTime = await db.DailyActivityRollups
            .Where(r => r.GuildId == guildId && r.UserId == userId)
            .SumAsync(r => (int?)r.VoiceMinutes, ct) ?? 0;

        var seasonId = await db.ActiveSeasonIdAsync(guildId, ct);
        if (seasonId is not { } sid)
        {
            return new MemberVoiceStats(SeasonMinutes: 0, AllTimeMinutes: allTime, SeasonRank: 0);
        }

        var seasonMinutes = await db.SeasonParticipations
            .Where(p => p.GuildId == guildId && p.SeasonId == sid && p.UserId == userId)
            .Select(p => (int?)p.VoiceMinutes).FirstOrDefaultAsync(ct) ?? 0;

        var rank = seasonMinutes == 0
            ? 0
            : 1 + await db.SeasonParticipations.CountAsync(
                p => p.GuildId == guildId && p.SeasonId == sid && p.VoiceMinutes > seasonMinutes, ct);

        return new MemberVoiceStats(seasonMinutes, allTime, rank);
    }

    /// <summary>Active sessions as a sortable/filterable/paged grid. Sort: name | present | attendees | started (default).</summary>
    public async Task<PagedResult<ActiveSessionView>> ActiveSessionsPageAsync(
        ulong guildId, string? search, string sort, bool desc, int page, int pageSize = 25, CancellationToken ct = default)
    {
        var q = db.TrackingSessions.Where(s => s.GuildId == guildId && s.Status == TrackingSessionStatus.Active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            q = q.Where(s => s.Name.Contains(search));
        }

        var total = await q.CountAsync(ct);

        q = (sort, desc) switch
        {
            ("name", false) => q.OrderBy(s => s.Name),
            ("name", true) => q.OrderByDescending(s => s.Name),
            ("attendees", false) => q.OrderBy(s => s.Attendance.Count),
            ("attendees", true) => q.OrderByDescending(s => s.Attendance.Count),
            ("present", false) => q.OrderBy(s => s.Attendance.Count(a => a.OpenSegmentStart != null)),
            ("present", true) => q.OrderByDescending(s => s.Attendance.Count(a => a.OpenSegmentStart != null)),
            (_, true) => q.OrderByDescending(s => s.StartedAt),
            _ => q.OrderBy(s => s.StartedAt),
        };

        var items = await Page(q, page, pageSize)
            .Select(s => new ActiveSessionView(
                s.Id, s.Name, s.Source, s.VoiceChannelId, s.VoiceChannelName, s.ScheduledEventId, s.StartedAt,
                s.Attendance.Count, s.Attendance.Count(a => a.OpenSegmentStart != null)))
            .ToListAsync(ct);

        return new PagedResult<ActiveSessionView>(items, page, pageSize, total);
    }

    /// <summary>Closed sessions as a sortable/filterable/paged grid. Sort: name | attendees | minutes | ended (default).</summary>
    public async Task<PagedResult<RecentSessionView>> RecentSessionsPageAsync(
        ulong guildId, string? search, string sort, bool desc, int page, int pageSize = 25, CancellationToken ct = default)
    {
        var q = db.TrackingSessions.Where(s => s.GuildId == guildId && s.Status == TrackingSessionStatus.Closed);
        if (!string.IsNullOrWhiteSpace(search))
        {
            q = q.Where(s => s.Name.Contains(search));
        }

        var total = await q.CountAsync(ct);

        q = (sort, desc) switch
        {
            ("name", false) => q.OrderBy(s => s.Name),
            ("name", true) => q.OrderByDescending(s => s.Name),
            ("attendees", false) => q.OrderBy(s => s.Attendance.Count),
            ("attendees", true) => q.OrderByDescending(s => s.Attendance.Count),
            ("minutes", false) => q.OrderBy(s => s.Attendance.Sum(a => a.TotalMinutes)),
            ("minutes", true) => q.OrderByDescending(s => s.Attendance.Sum(a => a.TotalMinutes)),
            (_, false) => q.OrderBy(s => s.EndedAt),
            _ => q.OrderByDescending(s => s.EndedAt),
        };

        var items = await Page(q, page, pageSize)
            .Select(s => new RecentSessionView(
                s.Id, s.Name, s.Source, s.VoiceChannelId, s.VoiceChannelName, s.StartedAt, s.EndedAt,
                s.Attendance.Count, s.Attendance.Sum(a => a.TotalMinutes)))
            .ToListAsync(ct);

        return new PagedResult<RecentSessionView>(items, page, pageSize, total);
    }

    /// <summary>
    /// The unified sessions datagrid: active + ended in one shape, filtered by status/name/source, sorted, paged.
    /// Sort keys: name | status | started (default) | ended | attendees | present | minutes.
    /// </summary>
    public async Task<PagedResult<SessionGridRow>> SessionsPageAsync(
        ulong guildId, SessionStatusFilter status, string? search, TrackingSessionSource? source,
        string sort, bool desc, int page, int pageSize = 25, CancellationToken ct = default)
    {
        var q = db.TrackingSessions.Where(s => s.GuildId == guildId);
        q = status switch
        {
            SessionStatusFilter.Active => q.Where(s => s.Status == TrackingSessionStatus.Active),
            SessionStatusFilter.Ended => q.Where(s => s.Status == TrackingSessionStatus.Closed),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            q = q.Where(s => s.Name.Contains(search));
        }
        if (source is { } src)
        {
            q = q.Where(s => s.Source == src);
        }

        var total = await q.CountAsync(ct);

        q = (sort, desc) switch
        {
            ("name", false) => q.OrderBy(s => s.Name),
            ("name", true) => q.OrderByDescending(s => s.Name),
            ("status", false) => q.OrderBy(s => s.Status),
            ("status", true) => q.OrderByDescending(s => s.Status),
            ("attendees", false) => q.OrderBy(s => s.Attendance.Count),
            ("attendees", true) => q.OrderByDescending(s => s.Attendance.Count),
            ("present", false) => q.OrderBy(s => s.Attendance.Count(a => a.InChannel)),
            ("present", true) => q.OrderByDescending(s => s.Attendance.Count(a => a.InChannel)),
            ("minutes", false) => q.OrderBy(s => s.Attendance.Sum(a => a.TotalMinutes)),
            ("minutes", true) => q.OrderByDescending(s => s.Attendance.Sum(a => a.TotalMinutes)),
            ("ended", false) => q.OrderBy(s => s.EndedAt),
            ("ended", true) => q.OrderByDescending(s => s.EndedAt),
            (_, false) => q.OrderBy(s => s.StartedAt),
            (_, true) => q.OrderByDescending(s => s.StartedAt),
        };

        var items = await Page(q, page, pageSize)
            .Select(s => new SessionGridRow(
                s.Id, s.Name, s.Source, s.VoiceChannelId, s.VoiceChannelName, s.StartedAt, s.EndedAt,
                s.Status == TrackingSessionStatus.Active,
                s.Attendance.Count,
                s.Attendance.Count(a => a.OpenSegmentStart != null),
                s.Attendance.Count(a => a.InChannel),
                s.Attendance.Sum(a => a.TotalMinutes)))
            .ToListAsync(ct);

        return new PagedResult<SessionGridRow>(items, page, pageSize, total);
    }

    /// <summary>A single session with its attendance roster (drill-in; works for active and closed). When
    /// <paramref name="includeEvents"/> is false the presence-event stream is skipped (lean payload for callers
    /// that only need the roster — e.g. the API base detail; events come from <see cref="SessionEventsAsync"/>).</summary>
    public async Task<SessionDetailView?> SessionDetailAsync(ulong guildId, Guid sessionId, bool includeEvents = true, CancellationToken ct = default)
    {
        var session = await db.TrackingSessions
            .Include(s => s.Attendance)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.GuildId == guildId, ct);
        if (session is null)
        {
            return null;
        }

        var ids = session.Attendance.Select(a => a.UserId).Append(session.OpenedBy).Distinct().ToList();
        var users = (await db.Users
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.Username, u.GlobalName, u.AvatarHash })
                .ToListAsync(ct))
            .ToDictionary(u => u.Id);

        var members = session.Attendance
            .OrderByDescending(a => a.TotalMinutes)
            .Select(a =>
            {
                users.TryGetValue(a.UserId, out var u);
                var rewardMinutes = a.WeightedSeconds > 0m ? (int)(a.WeightedSeconds / 60m) : a.TotalMinutes;
                return new SessionMemberRow(
                    a.UserId,
                    u?.GlobalName ?? u?.Username ?? $"User {a.UserId}",
                    u is null ? null : Discord.DiscordCdn.AvatarUrl(u.Id, u.AvatarHash),
                    a.FirstJoinedAt, a.TotalMinutes, rewardMinutes, a.OpenSegmentStart != null, a.InChannel, a.LastSeenAt);
            })
            .ToList();

        var rate = (await db.FindGuildAsync(guildId, ct))?.Settings.PointsPerVoiceMinute ?? TrackingSessionService.DefaultPointsPerMinute;

        string NameOf(ulong id) => users.TryGetValue(id, out var u) ? (u.GlobalName ?? u.Username ?? $"User {id}") : $"User {id}";
        IReadOnlyList<SessionPresenceEventRow> events = includeEvents
            ? (await db.SessionPresenceEvents
                    .Where(ev => ev.SessionId == sessionId)
                    .OrderBy(ev => ev.AtUtc).ThenBy(ev => ev.Id)
                    .Select(ev => new { ev.UserId, ev.Kind, ev.Reason, ev.AtUtc })
                    .ToListAsync(ct))
                .Select(ev => new SessionPresenceEventRow(ev.UserId, NameOf(ev.UserId), ev.Kind, ev.Reason, ev.AtUtc))
                .ToList()
            : [];

        return new SessionDetailView(
            session.Id, session.Name, session.Source, session.VoiceChannelId, session.VoiceChannelName, session.ScheduledEventId,
            session.StartedAt, session.EndedAt, session.Status == TrackingSessionStatus.Active, session.OpenedBy, NameOf(session.OpenedBy),
            session.Guards, rate > 0 ? rate : TrackingSessionService.DefaultPointsPerMinute, members, events);
    }

    /// <summary>A session's presence-event stream (audit log), paged chronologically. Null if the session
    /// isn't in this guild. Backs the API events endpoint; the web reads events inline via <see cref="SessionDetailAsync"/>.</summary>
    public async Task<PagedResult<SessionPresenceEventRow>?> SessionEventsAsync(
        ulong guildId, Guid sessionId, int page, int pageSize = 100, CancellationToken ct = default)
    {
        if (!await db.TrackingSessions.AnyAsync(s => s.Id == sessionId && s.GuildId == guildId, ct))
        {
            return null;
        }

        var q = db.SessionPresenceEvents.Where(ev => ev.SessionId == sessionId);
        var total = await q.CountAsync(ct);
        var page1 = await q
            .OrderBy(ev => ev.AtUtc).ThenBy(ev => ev.Id)
            .Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize)
            .Select(ev => new { ev.UserId, ev.Kind, ev.Reason, ev.AtUtc })
            .ToListAsync(ct);

        var names = await db.UserDisplayNameMapAsync(page1.Select(e => e.UserId).Distinct().ToList(), ct);
        var rows = page1
            .Select(ev => new SessionPresenceEventRow(
                ev.UserId, names.TryGetValue(ev.UserId, out var n) ? n : $"User {ev.UserId}", ev.Kind, ev.Reason, ev.AtUtc))
            .ToList();

        return new PagedResult<SessionPresenceEventRow>(rows, page, pageSize, total);
    }

    /// <summary>Monitored channels carrying background presence right now — with each channel's mode/rate and the
    /// present members, flagged as earning (reward-eligible) vs present-only. Powers the live Background tab.</summary>
    public async Task<IReadOnlyList<BackgroundChannelView>> BackgroundNowAsync(ulong guildId, CancellationToken ct = default)
    {
        var present = await db.BackgroundVoicePresences
            .Where(p => p.GuildId == guildId && p.ActiveOpenSegmentStart != null)
            .Select(p => new { p.ChannelId, p.UserId, Earning = p.OpenSegmentStart != null, p.AwardedPointsToday, p.PresentSince })
            .ToListAsync(ct);
        if (present.Count == 0)
        {
            return [];
        }

        var channelIds = present.Select(p => p.ChannelId).Distinct().ToList();
        var channels = (await db.GuildChannels
                .Where(c => c.GuildId == guildId && channelIds.Contains(c.ChannelId))
                .Select(c => new { c.ChannelId, c.Name, c.Mode, c.PointsPerMinute })
                .ToListAsync(ct))
            .ToDictionary(c => c.ChannelId);

        var userIds = present.Select(p => p.UserId).Distinct().ToList();
        var users = (await db.Users
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username, u.GlobalName, u.AvatarHash })
                .ToListAsync(ct))
            .ToDictionary(u => u.Id);

        // Cumulative active-time today per member+channel, from the daily rollups. ("Here since" comes from the
        // presence row's PresentSince, which — unlike the rollup — is time-precise for the current stint.)
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var minutesToday = (await db.DailyActivityRollups
                .Where(r => r.GuildId == guildId && r.Date == today && channelIds.Contains(r.ChannelId) && userIds.Contains(r.UserId))
                .Select(r => new { r.UserId, r.ChannelId, r.VoiceMinutes })
                .ToListAsync(ct))
            .ToDictionary(r => (r.UserId, r.ChannelId), r => r.VoiceMinutes);

        string NameOf(ulong id) => users.TryGetValue(id, out var u) ? (u.GlobalName ?? u.Username) : $"User {id}";
        string? AvatarOf(ulong id) => users.TryGetValue(id, out var u) ? Discord.DiscordCdn.AvatarUrl(u.Id, u.AvatarHash) : null;

        return present
            .GroupBy(p => p.ChannelId)
            .Select(g =>
            {
                channels.TryGetValue(g.Key, out var ch);
                var members = g
                    .OrderByDescending(x => x.Earning).ThenBy(x => NameOf(x.UserId), StringComparer.OrdinalIgnoreCase)
                    .Select(x => new BackgroundMemberRow(
                        x.UserId, NameOf(x.UserId), AvatarOf(x.UserId), x.Earning,
                        minutesToday.GetValueOrDefault((x.UserId, x.ChannelId)),
                        x.AwardedPointsToday,
                        x.PresentSince))
                    .ToList();
                return new BackgroundChannelView(g.Key, ch?.Name ?? string.Empty, ch?.Mode ?? TrackedChannelMode.Off, ch?.PointsPerMinute ?? 0, members);
            })
            .OrderBy(c => c.ChannelName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IQueryable<TrackingSession> Page(IQueryable<TrackingSession> q, int page, int pageSize)
        => q.Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize);

    /// <summary>Active sessions the member is currently in, from their own perspective (their minutes).</summary>
    public async Task<IReadOnlyList<MemberSessionView>> MemberActiveSessionsAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var rows = await (
            from a in db.VoiceAttendance
            join s in db.TrackingSessions on a.TrackingSessionId equals s.Id
            where s.GuildId == guildId && s.Status == TrackingSessionStatus.Active && a.UserId == userId
            orderby s.StartedAt
            select new { s.Id, s.Name, s.VoiceChannelName, a.TotalMinutes, Present = a.OpenSegmentStart != null, s.StartedAt })
            .ToListAsync(ct);

        return rows.Select(r => new MemberSessionView(r.Id, r.Name, r.VoiceChannelName, r.TotalMinutes, r.Present, r.StartedAt, null)).ToList();
    }

    /// <summary>The member's most recent finished sessions, from their own perspective (their minutes).</summary>
    public async Task<IReadOnlyList<MemberSessionView>> MemberSessionHistoryAsync(ulong guildId, ulong userId, int take = 25, CancellationToken ct = default)
    {
        var rows = await (
            from a in db.VoiceAttendance
            join s in db.TrackingSessions on a.TrackingSessionId equals s.Id
            where s.GuildId == guildId && s.Status == TrackingSessionStatus.Closed && a.UserId == userId
            orderby s.EndedAt descending
            select new { s.Id, s.Name, s.VoiceChannelName, a.TotalMinutes, s.StartedAt, s.EndedAt })
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(r => new MemberSessionView(r.Id, r.Name, r.VoiceChannelName, r.TotalMinutes, false, r.StartedAt, r.EndedAt)).ToList();
    }
}
