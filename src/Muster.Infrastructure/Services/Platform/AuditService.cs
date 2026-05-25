using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;

namespace Muster.Infrastructure.Services.Platform;

public record AuditQuery(
    string? Search = null,
    string? Action = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string Sort = "date",
    bool Desc = true,
    int Page = 1,
    int PageSize = 25);

public record AuditEntryView(
    long Id, ulong ActorUserId, string ActorName, string Action, string? Details, DateTimeOffset OccurredAt);

public record AuditPage(IReadOnlyList<AuditEntryView> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => (int)Math.Ceiling(Total / (double)Math.Max(PageSize, 1));
}

/// <summary>
/// Records and queries the admin audit trail. Search/filter/sort/paging run server-side (static SSR
/// friendly) and the same filter drives CSV export.
/// </summary>
public class AuditService(MusterDbContext db)
{
    public async Task RecordAsync(
        ulong guildId, ulong actorUserId, string action, string? details = null, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            GuildId = guildId,
            ActorUserId = actorUserId,
            Action = action,
            Details = details,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

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

    private async Task<List<AuditEntryView>> ToViewsAsync(List<AuditLog> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.ActorUserId).Distinct().ToList();
        var names = await db.Users
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.GlobalName ?? u.Username, ct);

        return rows
            .Select(r => new AuditEntryView(
                r.Id, r.ActorUserId, names.GetValueOrDefault(r.ActorUserId, r.ActorUserId.ToString()),
                r.Action, r.Details, r.OccurredAt))
            .ToList();
    }
}
