using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muster.Domain.Enums;
using Muster.Infrastructure.Discord;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;

namespace Muster.Infrastructure.Services.Platform;

/// <summary>Per-host default <see cref="AuditOrigin"/> — the Web host registers UI, the bot Bot, etc. The
/// audit service falls back to this when a call site doesn't override the origin.</summary>
public interface IAuditOriginProvider
{
    AuditOrigin Default { get; }
}

public sealed class AuditOriginProvider(AuditOrigin defaultOrigin) : IAuditOriginProvider
{
    public AuditOrigin Default { get; } = defaultOrigin;
}

public record AuditQuery(
    string? Search = null,
    string? Action = null,
    IReadOnlyCollection<AuditCategory>? Categories = null,
    IReadOnlyCollection<AuditOrigin>? Origins = null,
    AuditOutcome? Outcome = null,
    ulong? TargetUserId = null,
    ulong? ActorUserId = null,
    ulong? MemberUserId = null,
    string? CorrelationId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string Sort = "date",
    bool Desc = true,
    int Page = 1,
    int PageSize = 25);

/// <summary><see cref="Category"/> and <see cref="Area"/> are derived from the action key via
/// <see cref="AuditActionMetadata"/> at read time — no schema migration to add them. <see cref="Origin"/>,
/// <see cref="Outcome"/>, <see cref="TargetUserId"/>, and <see cref="CorrelationId"/> are stored columns.</summary>
public record AuditEntryView(
    long Id,
    ulong ActorUserId, string ActorName, string? ActorAvatarUrl,
    ulong? TargetUserId,
    string Action, AuditCategory Category, string Area,
    AuditOrigin Origin, AuditOutcome Outcome, string? CorrelationId,
    string? Details, DateTimeOffset OccurredAt);

/// <summary>At-a-glance counters + tiny chart series for the audit hub. Each field is one card or one chart;
/// keep this lean so the table still dominates the page.</summary>
public record AuditKpis(
    int Last24h,
    int Last7d,
    string? TopAction7d,
    int TopAction7dCount,
    int Last24hDenied,
    int Last24hFailed,
    ulong? TopActor7dUserId,
    string? TopActor7dName,
    int TopActor7dCount,
    IReadOnlyList<AuditDailyBucket> Last14Days,
    IReadOnlyList<AuditOriginSlice> OriginLast7d);

/// <summary>One day's audit volume — drives the 14-day mini bar chart.</summary>
public record AuditDailyBucket(DateOnly Date, int Count);

/// <summary>How many rows came from one Origin in the trailing window — drives the segmented breakdown bar.</summary>
public record AuditOriginSlice(AuditOrigin Origin, int Count);

public record AuditPage(IReadOnlyList<AuditEntryView> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling(Total / (double)Math.Max(PageSize, 1));
}

/// <summary>
/// Records and queries the admin audit trail. Search/filter/sort/paging run server-side (static SSR
/// friendly) and the same filter drives CSV export.
/// </summary>
public class AuditService(MusterDbContext db, IAuditOriginProvider origins)
{
    /// <summary>Test convenience: omit the origin provider and fall back to Unknown. Real hosts always inject one.</summary>
    public AuditService(MusterDbContext db) : this(db, new AuditOriginProvider(Muster.Domain.Enums.AuditOrigin.Unknown)) { }

    // Compact JSON for typed payloads — camelCase keys + skip nulls to keep stored rows small.
    private static readonly JsonSerializerOptions _payloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task RecordAsync(
        ulong guildId, ulong actorUserId, string action, string? details = null,
        AuditOrigin? origin = null, AuditOutcome outcome = AuditOutcome.Success,
        ulong? targetUserId = null, string? correlationId = null,
        CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            GuildId = guildId,
            ActorUserId = actorUserId,
            TargetUserId = targetUserId,
            Action = action,
            Details = details,
            Origin = origin ?? origins.Default,
            Outcome = outcome,
            CorrelationId = correlationId ?? AmbientCorrelationId(),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Strongly-typed action + plain-text details. Recommended path for new call sites.</summary>
    public Task RecordAsync(
        ulong guildId, ulong actorUserId, AuditAction action, string? details = null,
        AuditOrigin? origin = null, AuditOutcome outcome = AuditOutcome.Success,
        ulong? targetUserId = null, string? correlationId = null,
        CancellationToken ct = default)
        => RecordAsync(guildId, actorUserId, action.Key, details, origin, outcome, targetUserId, correlationId, ct);

    /// <summary>Typed payload — serializes <paramref name="payload"/> to JSON and stores in <c>Details</c>.
    /// The web layer's default formatter auto-detects JSON for rendering.</summary>
    public Task RecordAsync<T>(
        ulong guildId, ulong actorUserId, AuditAction action, T payload,
        AuditOrigin? origin = null, AuditOutcome outcome = AuditOutcome.Success,
        ulong? targetUserId = null, string? correlationId = null,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, _payloadJson);
        return RecordAsync(guildId, actorUserId, action.Key, json, origin, outcome, targetUserId, correlationId, ct);
    }

    /// <summary>OpenTelemetry already wraps every HTTP request / Wolverine handler in an Activity, so its trace id
    /// is the natural correlation key — same value the logs and traces use. Returns null when no activity is
    /// active (e.g. a background job that didn't start its own span).</summary>
    private static string? AmbientCorrelationId() =>
        System.Diagnostics.Activity.Current?.TraceId.ToHexString();

    public async Task<AuditPage> SearchAsync(ulong guildId, AuditQuery query, CancellationToken ct = default)
    {
        var filtered = Filter(guildId, query);
        var total = await filtered.CountAsync(ct);

        var page = Math.Max(query.Page, 1);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var rows = await Sort(filtered, query)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return new AuditPage(await ToViewsAsync(rows, ct), total, page, size);
    }

    /// <summary>Rows matching the filter (ignoring paging), for export. Capped to avoid huge downloads.</summary>
    public async Task<IReadOnlyList<AuditEntryView>> ExportAsync(
        ulong guildId, AuditQuery query, int max = 10000, CancellationToken ct = default)
    {
        var rows = await Sort(Filter(guildId, query), query).Take(max).ToListAsync(ct);
        return await ToViewsAsync(rows, ct);
    }

    public async Task<IReadOnlyList<string>> GetActionsAsync(ulong guildId, CancellationToken ct = default)
        => await db.AuditLogs
            .Where(a => a.GuildId == guildId)
            .Select(a => a.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(ct);

    private IQueryable<AuditLog> Filter(ulong guildId, AuditQuery query)
    {
        var q = db.AuditLogs.Where(a => a.GuildId == guildId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search;
            q = q.Where(a => a.Action.Contains(s) || (a.Details != null && a.Details.Contains(s)));
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            q = q.Where(a => a.Action == query.Action);
        }

        if (query.Origins is { Count: > 0 } origins)
        {
            q = q.Where(a => origins.Contains(a.Origin));
        }

        if (query.Categories is { Count: > 0 } categories)
        {
            // Category lives in code (the AuditActions registry), not the DB. Resolve the matching keys + filter in-DB.
            var cats = categories.ToHashSet();
            var keys = AuditActionMetadata.All
                .Where(a => cats.Contains(a.Category))
                .Select(a => a.Key)
                .ToList();
            // Filtering by Other = any action that isn't in the registry.
            if (cats.Contains(AuditCategory.Other))
            {
                var known = AuditActionMetadata.All.Select(a => a.Key).ToHashSet();
                q = q.Where(a => keys.Contains(a.Action) || !known.Contains(a.Action));
            }
            else
            {
                q = keys.Count == 0 ? q.Where(_ => false) : q.Where(a => keys.Contains(a.Action));
            }
        }

        if (query.Outcome is { } outcome)
        {
            q = q.Where(a => a.Outcome == outcome);
        }

        if (query.TargetUserId is { } target)
        {
            q = q.Where(a => a.TargetUserId == target);
        }

        if (query.ActorUserId is { } actor)
        {
            q = q.Where(a => a.ActorUserId == actor);
        }

        if (query.MemberUserId is { } member)
        {
            q = q.Where(a => a.ActorUserId == member || a.TargetUserId == member);
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            q = q.Where(a => a.CorrelationId == query.CorrelationId);
        }

        if (query.From is { } from)
        {
            var fromTs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            q = q.Where(a => a.OccurredAt >= fromTs);
        }

        if (query.To is { } to)
        {
            var toTs = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            q = q.Where(a => a.OccurredAt < toTs);
        }

        return q;
    }

    private static IQueryable<AuditLog> Sort(IQueryable<AuditLog> q, AuditQuery query) => (query.Sort, query.Desc) switch
    {
        ("action", true) => q.OrderByDescending(a => a.Action).ThenByDescending(a => a.Id),
        ("action", false) => q.OrderBy(a => a.Action).ThenBy(a => a.Id),
        (_, false) => q.OrderBy(a => a.OccurredAt).ThenBy(a => a.Id),
        _ => q.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id),
    };

    /// <summary>Compact dashboard counters + tiny chart series. Six SQL queries; each is keyed by guild and bounded
    /// to a fixed window so the cost stays flat regardless of total audit history.</summary>
    public async Task<AuditKpis> GetKpisAsync(ulong guildId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var dayAgo = now.AddHours(-24);
        var weekAgo = now.AddDays(-7);
        var fortnightAgo = now.AddDays(-14);

        // 1) Header counts.
        var last24h = await db.AuditLogs.CountAsync(a => a.GuildId == guildId && a.OccurredAt >= dayAgo, ct);
        var last7d = await db.AuditLogs.CountAsync(a => a.GuildId == guildId && a.OccurredAt >= weekAgo, ct);

        // 2) Last-24h outcome split — only count failures + denials so the row reads as a security signal.
        var denied24h = await db.AuditLogs
            .CountAsync(a => a.GuildId == guildId && a.OccurredAt >= dayAgo && a.Outcome == AuditOutcome.Denied, ct);
        var failed24h = await db.AuditLogs
            .CountAsync(a => a.GuildId == guildId && a.OccurredAt >= dayAgo && a.Outcome == AuditOutcome.Failed, ct);

        // 3) Top action over the last 7 days.
        var topAction = await db.AuditLogs
            .Where(a => a.GuildId == guildId && a.OccurredAt >= weekAgo)
            .GroupBy(a => a.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .FirstOrDefaultAsync(ct);

        // 4) Top actor over the last 7 days — name resolved via the user lookup the formatter chain already uses.
        var topActor = await db.AuditLogs
            .Where(a => a.GuildId == guildId && a.OccurredAt >= weekAgo)
            .GroupBy(a => a.ActorUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .FirstOrDefaultAsync(ct);

        string? topActorName = null;
        if (topActor is not null)
        {
            var map = await db.UserDisplayNameMapAsync([topActor.UserId], ct);
            topActorName = map.GetValueOrDefault(topActor.UserId, topActor.UserId.ToString());
        }

        // 5) Daily volume buckets (last 14 days). DateDiffDay translates to SQL DATEDIFF(day, ...); using a fixed
        // anchor at the start of today gives one int per row identifying its day-offset relative to "today" — which
        // we map back to a DateOnly in memory below. EF can't translate `new DateOnly(...)` or DateTime ctors.
        var todayAnchor = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var rawDaily = await db.AuditLogs
            .Where(a => a.GuildId == guildId && a.OccurredAt >= fortnightAgo)
            .GroupBy(a => EF.Functions.DateDiffDay(a.OccurredAt, todayAnchor))
            .Select(g => new { DaysAgo = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var dailyByDate = rawDaily.ToDictionary(r => today.AddDays(-r.DaysAgo), r => r.Count);
        var dailyBuckets = Enumerable.Range(0, 14)
            .Select(offset => today.AddDays(-13 + offset))
            .Select(d => new AuditDailyBucket(d, dailyByDate.GetValueOrDefault(d, 0)))
            .ToList();

        // 6) Origin breakdown over the last 7 days — drives the segmented bar.
        var rawOrigin = await db.AuditLogs
            .Where(a => a.GuildId == guildId && a.OccurredAt >= weekAgo)
            .GroupBy(a => a.Origin)
            .Select(g => new { Origin = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var originSlices = rawOrigin
            .Select(r => new AuditOriginSlice(r.Origin, r.Count))
            .OrderByDescending(s => s.Count)
            .ToList();

        return new AuditKpis(
            last24h, last7d,
            topAction?.Action, topAction?.Count ?? 0,
            denied24h, failed24h,
            topActor?.UserId, topActorName, topActor?.Count ?? 0,
            dailyBuckets, originSlices);
    }

    private async Task<List<AuditEntryView>> ToViewsAsync(List<AuditLog> rows, CancellationToken ct)
    {
        // Resolve actors + targets in one batch — same query for both kinds.
        var ids = rows.SelectMany(r => r.TargetUserId is { } t ? new[] { r.ActorUserId, t } : new[] { r.ActorUserId })
            .Distinct().ToList();
        var users = await db.UserDisplayMapAsync(ids, ct);

        return rows
            .Select(r =>
            {
                var u = users.GetValueOrDefault(r.ActorUserId);
                var name = u.Name ?? r.ActorUserId.ToString();
                var avatar = DiscordCdn.AvatarUrl(r.ActorUserId, u.AvatarHash);
                var meta = AuditActionMetadata.Resolve(r.Action);
                return new AuditEntryView(
                    r.Id,
                    r.ActorUserId, name, avatar,
                    r.TargetUserId,
                    r.Action, meta.Category, meta.Area,
                    r.Origin, r.Outcome, r.CorrelationId,
                    r.Details, r.OccurredAt);
            })
            .ToList();
    }
}
