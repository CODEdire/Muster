using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>A member's rank in the voice-participation leaderboard.</summary>
public record VoiceLeaderboardEntry(ulong UserId, int VoiceMinutes);

/// <summary>An in-progress tracking session (live ops view).</summary>
public record ActiveSessionView(
    Guid Id, string Name, TrackingSessionSource Source, ulong VoiceChannelId, ulong? ScheduledEventId,
    DateTimeOffset StartedAt, int Attendees, int PresentNow);

/// <summary>A finished tracking session (history view).</summary>
public record RecentSessionView(
    Guid Id, string Name, TrackingSessionSource Source, ulong VoiceChannelId,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int Attendees, int TotalMinutes);

/// <summary>A member's own voice participation summary (self-view).</summary>
public record MemberVoiceStats(int SeasonMinutes, int AllTimeMinutes, int SeasonRank);

/// <summary>A page of grid rows plus the totals a pager needs.</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

/// <summary>One member's row within a session's attendance roster (drill-in detail).</summary>
public record SessionMemberRow(ulong UserId, DateTimeOffset FirstJoinedAt, int TotalMinutes, bool PresentNow);

/// <summary>A single session with its full attendance roster (serves active + closed).</summary>
public record SessionDetailView(
    Guid Id, string Name, TrackingSessionSource Source, ulong VoiceChannelId,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool Active, IReadOnlyList<SessionMemberRow> Members);

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
    /// <summary>Top members by voice minutes — the active season when one is open, else all time.</summary>
    public async Task<IReadOnlyList<VoiceLeaderboardEntry>> VoiceLeaderboardAsync(
        ulong guildId, int top = 10, CancellationToken ct = default)
    {
        var seasonId = await db.ActiveSeasonIdAsync(guildId, ct);

        if (seasonId is { } sid)
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
                s.Id, s.Name, s.Source, s.VoiceChannelId, s.ScheduledEventId, s.StartedAt,
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
                s.Id, s.Name, s.Source, s.VoiceChannelId, s.StartedAt, s.EndedAt,
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
                s.Id, s.Name, s.Source, s.VoiceChannelId, s.ScheduledEventId, s.StartedAt,
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
                s.Id, s.Name, s.Source, s.VoiceChannelId, s.StartedAt, s.EndedAt,
                s.Attendance.Count, s.Attendance.Sum(a => a.TotalMinutes)))
            .ToListAsync(ct);

        return new PagedResult<RecentSessionView>(items, page, pageSize, total);
    }

    /// <summary>A single session with its attendance roster (drill-in; works for active and closed).</summary>
    public async Task<SessionDetailView?> SessionDetailAsync(ulong guildId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.TrackingSessions
            .Include(s => s.Attendance)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.GuildId == guildId, ct);
        if (session is null)
        {
            return null;
        }

        var members = session.Attendance
            .OrderByDescending(a => a.TotalMinutes)
            .Select(a => new SessionMemberRow(a.UserId, a.FirstJoinedAt, a.TotalMinutes, a.OpenSegmentStart != null))
            .ToList();

        return new SessionDetailView(
            session.Id, session.Name, session.Source, session.VoiceChannelId,
            session.StartedAt, session.EndedAt, session.Status == TrackingSessionStatus.Active, members);
    }

    private static IQueryable<TrackingSession> Page(IQueryable<TrackingSession> q, int page, int pageSize)
        => q.Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize);
}
