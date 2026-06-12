using Microsoft.EntityFrameworkCore;
using Muster.Domain;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Discord;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Tracking;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Web;

/// <summary>A member's points snapshot for the Points page (current balance + voice context).</summary>
public record PointsSnapshot(long Balance, bool Seasonal, MemberVoiceStats Voice);

/// <summary>Points earned guild-wide for one ledger source — the participation "points by source" rows.</summary>
public record SourceEarned(CurrencyLedgerSource Source, long Total);

/// <summary>Guild participation overview for a season: total awarded, distinct active earners, and the by-source split.</summary>
public record ParticipationOverview(long Awarded, int Earners, IReadOnlyList<SourceEarned> BySource);

/// <summary>Engagement metrics for one season: volume, reach, and the acquisition/retention split. Participation rate
/// is earners ÷ current member count; retention is returning ÷ the previous season's earners.</summary>
public record SeasonEngagement(
    SeasonInfo Season, long Awarded, int Earners, int MemberCount,
    int NewEarners, int ReturningEarners, int PriorEarners)
{
    public int ParticipationPct => MemberCount > 0 ? (int)Math.Round(Earners * 100.0 / MemberCount) : 0;
    public int RetentionPct => PriorEarners > 0 ? (int)Math.Round(ReturningEarners * 100.0 / PriorEarners) : 0;
}

/// <summary>The Participation home aggregate: cross-season health plus the per-season engagement breakdown
/// (oldest → newest), with the current + previous seasons resolved for the dedicated tabs.</summary>
public record ParticipationHome(
    long LifetimeAwarded, int MembersEverEarned, int MemberCount,
    IReadOnlyList<SeasonEngagement> Seasons, SeasonEngagement? Current, SeasonEngagement? Previous);

/// <summary>One point on the weekly-velocity chart for a season.</summary>
public record WeeklyPoint(string Label, long Total);

/// <summary>One row of the full points ledger — a member movement tagged with the season it landed in.</summary>
public record PointsLedgerRow(
    ulong UserId, string DisplayName, string? AvatarUrl, long Amount,
    CurrencyLedgerSource Source, DateTimeOffset OccurredAt, string Reason, Guid? SeasonId, string SeasonName);

/// <summary>
/// The Points surface: only POINTS. Same storage as other currencies but a dedicated read service so callers
/// can never accidentally surface points on the wallet (or vice versa). Wraps <see cref="ICurrencyReadService"/>
/// + the ledger queries with the POINTS currency id resolved internally.
/// </summary>
public class PointsReadService(MusterDbContext db, ICurrencyReadService scores, ParticipationReadService participation)
{
    /// <summary>A member's current points balance + voice stats for the Personal Points tab.</summary>
    public async Task<PointsSnapshot> GetSnapshotAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var voice = await participation.MemberVoiceStatsAsync(guildId, userId, ct);
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return new PointsSnapshot(0, Seasonal: false, voice);
        }

        Guid? seasonId = points.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
        var balance = await db.BalanceAsync(guildId, userId, points.Id, seasonId, ct);
        return new PointsSnapshot(balance, points.IsSeasonal, voice);
    }

    /// <summary>Guild seasons for the participation season picker — empty when POINTS isn't seasonal.</summary>
    public async Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(ulong guildId, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        return points is { IsSeasonal: true } ? await db.SeasonsAsync(guildId, ct) : [];
    }

    /// <summary>Guild-wide participation overview for a season (default active): points awarded, active earners, and
    /// the points-by-source breakdown.</summary>
    public async Task<ParticipationOverview> GetParticipationAsync(ulong guildId, Guid? season, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return new ParticipationOverview(0, 0, []);
        }

        var scope = points.IsSeasonal ? (season ?? await db.ActiveSeasonIdAsync(guildId, ct)) : null;
        var bySourceMap = await db.GuildSourceEarnedAsync(guildId, points.Id, scope, ct);
        var bySource = bySourceMap.OrderByDescending(kv => kv.Value).Select(kv => new SourceEarned(kv.Key, kv.Value)).ToList();
        var earners = await db.GuildActiveEarnersAsync(guildId, points.Id, scope, ct);
        return new ParticipationOverview(bySourceMap.Values.Sum(), earners, bySource);
    }

    /// <summary>Guild-wide points awarded per season (season-over-season chart), oldest first.</summary>
    public async Task<IReadOnlyList<(SeasonInfo Season, long Total)>> GetSeasonTotalsAsync(ulong guildId, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is not { IsSeasonal: true })
        {
            return [];
        }

        var seasons = await db.SeasonsAsync(guildId, ct);
        var totals = await db.GuildSeasonEarnedAsync(guildId, points.Id, ct);
        return seasons.OrderBy(s => s.StartsAt).Select(s => (s, totals.GetValueOrDefault(s.Id, 0L))).ToList();
    }

    /// <summary>The Participation home aggregate: lifetime + per-season engagement (volume, participation rate,
    /// new vs returning earners, retention). Empty when POINTS isn't seasonal.</summary>
    public async Task<ParticipationHome> GetEngagementAsync(ulong guildId, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is not { IsSeasonal: true })
        {
            return new ParticipationHome(0, 0, 0, [], null, null);
        }

        var seasons = (await db.SeasonsAsync(guildId, ct)).OrderBy(s => s.StartsAt).ToList();
        var totals = await db.GuildSeasonEarnedAsync(guildId, points.Id, ct);
        var earnerIds = await db.GuildSeasonEarnerIdsAsync(guildId, points.Id, ct);
        var memberCount = await db.GuildMembers.CountAsync(m => m.GuildId == guildId, ct);

        var rows = new List<SeasonEngagement>(seasons.Count);
        var seenBefore = new HashSet<ulong>();
        HashSet<ulong> prevIds = [];
        foreach (var s in seasons)
        {
            var ids = earnerIds.GetValueOrDefault(s.Id, []);
            var newCount = ids.Count(id => !seenBefore.Contains(id));
            var returning = ids.Count(prevIds.Contains);
            rows.Add(new SeasonEngagement(
                s, totals.GetValueOrDefault(s.Id, 0L), ids.Count, memberCount,
                newCount, returning, prevIds.Count));

            seenBefore.UnionWith(ids);
            prevIds = ids;
        }

        var lifetime = rows.Sum(r => r.Awarded);
        var current = rows.LastOrDefault(r => r.Season.IsActive) ?? rows.LastOrDefault();
        SeasonEngagement? previous = null;
        if (current is not null)
        {
            var idx = rows.FindIndex(r => r.Season.Id == current.Season.Id);
            if (idx > 0)
            {
                previous = rows[idx - 1];
            }
        }

        return new ParticipationHome(lifetime, seenBefore.Count, memberCount, rows, current, previous);
    }

    /// <summary>Weekly points-awarded series for one season (week 1 = the season's first 7 days), for the velocity chart.</summary>
    public async Task<IReadOnlyList<WeeklyPoint>> GetSeasonWeeklyAsync(ulong guildId, Guid seasonId, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return [];
        }

        var season = (await db.SeasonsAsync(guildId, ct)).FirstOrDefault(s => s.Id == seasonId);
        if (season is null)
        {
            return [];
        }

        var daily = await db.GuildSeasonDailyEarnedAsync(guildId, points.Id, seasonId, ct);
        if (daily.Count == 0)
        {
            return [];
        }

        var start = DateOnly.FromDateTime(season.StartsAt.UtcDateTime);
        var lastDay = daily.Keys.Max();
        var weeks = Math.Max(1, (lastDay.DayNumber - start.DayNumber) / 7 + 1);
        var buckets = new long[weeks];
        foreach (var (day, total) in daily)
        {
            var w = Math.Clamp((day.DayNumber - start.DayNumber) / 7, 0, weeks - 1);
            buckets[w] += total;
        }

        return buckets.Select((t, i) => new WeeklyPoint($"W{i + 1}", t)).ToList();
    }

    /// <summary>Top points earners — season-scoped when <paramref name="seasonId"/> is set, else summed across all seasons.</summary>
    public async Task<IReadOnlyList<LeaderboardRow>> GetTopEarnersAsync(ulong guildId, Guid? seasonId, int take, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return [];
        }

        var raw = seasonId is { } sid
            ? await db.GuildTopHoldersLedgerAsync(guildId, points.Id, sid, take, ct)
            : await db.GuildTopEarnersAllSeasonsAsync(guildId, points.Id, take, ct);

        var users = await db.UserDisplayMapAsync(raw.Select(r => r.UserId).ToList(), ct);
        return raw.Select((r, i) =>
        {
            var u = users.GetValueOrDefault(r.UserId);
            return new LeaderboardRow(i + 1, r.UserId, u.Name ?? r.UserId.ToString(), r.Total, DiscordCdn.AvatarUrl(r.UserId, u.AvatarHash));
        }).ToList();
    }

    /// <summary>Points-by-source split — season-scoped when <paramref name="seasonId"/> is set, else all-time.</summary>
    public async Task<IReadOnlyList<SourceEarned>> GetSourcesAsync(ulong guildId, Guid? seasonId, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return [];
        }

        var map = await db.GuildSourceEarnedAsync(guildId, points.Id, seasonId, ct);
        return map.OrderByDescending(kv => kv.Value).Select(kv => new SourceEarned(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Paged points history for the Personal Points tab.</summary>
    public async Task<PagedResult<MemberLedgerRow>> GetHistoryPageAsync(
        ulong guildId, ulong userId, string? search, string sortKey, bool descending,
        int page, int pageSize, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int? sign = null, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return new PagedResult<MemberLedgerRow>([], page, pageSize, 0);
        }

        var size = Math.Clamp(pageSize, 1, 100);
        var p = Math.Max(page, 1);
        var skip = (p - 1) * size;
        var season = points.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;

        var (rows, total) = await db.MemberLedgerPagedAsync(
            guildId, userId, points.Id, search, sortKey, descending, skip, size, ct,
            sources: sources, from: from, to: to, sign: sign, seasonScope: season);

        var items = rows
            .Select(r => new MemberLedgerRow(points.Code, r.Amount, r.SourceType, r.OccurredAt, r.Reason, null, r.Id, r.SourceId))
            .ToList();

        return new PagedResult<MemberLedgerRow>(items, p, size, total);
    }

    /// <summary>Σ in / Σ out for the POINTS ledger under the same filter — the datagrid footer totals.</summary>
    public async Task<(long In, long Out)> GetHistoryTotalsAsync(
        ulong guildId, ulong userId, string? search, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int? sign = null, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return (0, 0);
        }

        var season = points.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
        return await db.MemberLedgerTotalsAsync(guildId, userId, points.Id, search, ct, sources: sources, from: from, to: to, sign: sign, seasonScope: season);
    }

    /// <summary>All filtered POINTS rows (capped) for a CSV export of the current view.</summary>
    public async Task<IReadOnlyList<MemberLedgerRow>> GetHistoryForExportAsync(
        ulong guildId, ulong userId, string? search, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int? sign = null, int cap = 10000, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return [];
        }

        var season = points.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
        var rows = await db.MemberLedgerAllAsync(guildId, userId, points.Id, search, cap, ct, sources: sources, from: from, to: to, sign: sign, seasonScope: season);
        return rows.Select(r => new MemberLedgerRow(points.Code, r.Amount, r.SourceType, r.OccurredAt, r.Reason, null, r.Id, r.SourceId)).ToList();
    }

    /// <summary>Supply analytics for POINTS (or null when POINTS isn't configured in this guild).</summary>
    public Task<CurrencySupply?> GetSupplyAsync(ulong guildId, CancellationToken ct = default)
        => scores.GetSupplyAsync(guildId, CurrencyCodes.PointsCode, ct);

    /// <summary>Paged top holders of POINTS (escrow excluded). <paramref name="season"/> scopes the ranking to a
    /// specific season; null falls back to the active season for seasonal points.</summary>
    public async Task<PagedResult<LeaderboardRow>> GetTopHoldersPageAsync(
        ulong guildId, int page, int pageSize, Guid? season = null, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return new PagedResult<LeaderboardRow>([], page, pageSize, 0);
        }

        Guid? seasonId = season ?? (points.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null);
        var size = Math.Clamp(pageSize, 1, 100);
        var p = Math.Max(page, 1);
        var skip = (p - 1) * size;

        var (rows, total) = await db.TopWalletBalancesPagedAsync(
            guildId, points.Id, seasonId, CurrencyService.EscrowAccountUserId, skip, size, ct);

        var ids = rows.Select(r => r.UserId).ToList();
        var users = await db.UserDisplayMapAsync(ids, ct);

        var items = rows
            .Select((r, i) =>
            {
                var u = users.GetValueOrDefault(r.UserId);
                return new LeaderboardRow(
                    skip + i + 1, r.UserId,
                    u.Name ?? r.UserId.ToString(),
                    r.Balance,
                    DiscordCdn.AvatarUrl(r.UserId, u.AvatarHash));
            })
            .ToList();

        return new PagedResult<LeaderboardRow>(items, p, size, total);
    }

    /// <summary>Paged guild-wide POINTS movements.</summary>
    public async Task<PagedResult<MovementRow>> GetMovementsPageAsync(
        ulong guildId, string? search, string sortKey, bool descending,
        int page, int pageSize, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? season = null, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return new PagedResult<MovementRow>([], page, pageSize, 0);
        }

        var size = Math.Clamp(pageSize, 1, 100);
        var p = Math.Max(page, 1);
        var skip = (p - 1) * size;

        var (rows, total) = await db.GuildLedgerPagedAsync(
            guildId, points.Id, search, sortKey, descending, skip, size, ct,
            sources: sources, from: from, to: to, seasonScope: season);

        var ids = rows.Select(r => r.UserId).Distinct().ToList();
        var users = await db.UserDisplayMapAsync(ids, ct);
        string Name(ulong id) => id == CurrencyService.EscrowAccountUserId
            ? "Escrow (house)"
            : users.TryGetValue(id, out var u) ? u.Name : id.ToString();
        string? Avatar(ulong id) => id == CurrencyService.EscrowAccountUserId
            ? null
            : users.TryGetValue(id, out var u) ? DiscordCdn.AvatarUrl(id, u.AvatarHash) : null;

        var items = rows
            .Select(r => new MovementRow(
                r.UserId, Name(r.UserId), Avatar(r.UserId), points.Code, r.Amount,
                r.SourceType, r.OccurredAt, r.Reason))
            .ToList();

        return new PagedResult<MovementRow>(items, p, size, total);
    }

    /// <summary>Paged full points ledger (season-tagged) for the Ledger tab — sortable / filterable / season-scoped.</summary>
    public async Task<PagedResult<PointsLedgerRow>> GetLedgerPageAsync(
        ulong guildId, string? search, string sortKey, bool descending, int page, int pageSize,
        IReadOnlyCollection<CurrencyLedgerSource>? sources = null, DateTimeOffset? from = null,
        DateTimeOffset? to = null, Guid? season = null, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return new PagedResult<PointsLedgerRow>([], page, pageSize, 0);
        }

        var size = Math.Clamp(pageSize, 1, 100);
        var p = Math.Max(page, 1);
        var skip = (p - 1) * size;

        var (rows, total) = await db.GuildLedgerPagedAsync(
            guildId, points.Id, search, sortKey, descending, skip, size, ct,
            sources: sources, from: from, to: to, seasonScope: season);

        var items = await BuildLedgerRowsAsync(
            guildId, rows.Select(r => (r.UserId, r.Amount, r.SourceType, r.OccurredAt, r.Reason, r.SeasonId)).ToList(), ct);
        return new PagedResult<PointsLedgerRow>(items, p, size, total);
    }

    /// <summary>Σ earned / Σ spent for the points ledger under the same filter — the Ledger footer totals.</summary>
    public async Task<(long In, long Out)> GetLedgerTotalsAsync(
        ulong guildId, string? search, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, Guid? season = null, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        return points is null
            ? (0, 0)
            : await db.GuildLedgerTotalsAsync(guildId, points.Id, search, ct, sources: sources, from: from, to: to, seasonScope: season);
    }

    /// <summary>All filtered points-ledger rows (capped) for a CSV export of the current view.</summary>
    public async Task<IReadOnlyList<PointsLedgerRow>> GetLedgerForExportAsync(
        ulong guildId, string? search, string sortKey, bool descending,
        IReadOnlyCollection<CurrencyLedgerSource>? sources = null, DateTimeOffset? from = null,
        DateTimeOffset? to = null, Guid? season = null, int cap = 10000, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return [];
        }

        var rows = await db.GuildLedgerExportAsync(
            guildId, points.Id, search, sortKey, descending,
            sources: sources, from: from, to: to, seasonScope: season, cap: cap, ct: ct);
        return await BuildLedgerRowsAsync(guildId, rows, ct);
    }

    private async Task<List<PointsLedgerRow>> BuildLedgerRowsAsync(
        ulong guildId,
        List<(ulong UserId, long Amount, CurrencyLedgerSource Source, DateTimeOffset OccurredAt, string Reason, Guid? SeasonId)> rows,
        CancellationToken ct)
    {
        var ids = rows.Select(r => r.UserId).Distinct().ToList();
        var users = await db.UserDisplayMapAsync(ids, ct);
        var seasons = (await db.SeasonsAsync(guildId, ct)).ToDictionary(s => s.Id, s => s.Name);

        string Name(ulong id) => id == CurrencyService.EscrowAccountUserId
            ? "Escrow (house)"
            : users.TryGetValue(id, out var u) ? u.Name : id.ToString();
        string? Avatar(ulong id) => id == CurrencyService.EscrowAccountUserId
            ? null
            : users.TryGetValue(id, out var u) ? DiscordCdn.AvatarUrl(id, u.AvatarHash) : null;

        return rows.Select(r => new PointsLedgerRow(
            r.UserId, Name(r.UserId), Avatar(r.UserId), r.Amount, r.Source, r.OccurredAt, r.Reason,
            r.SeasonId, r.SeasonId is { } sid && seasons.TryGetValue(sid, out var sn) ? sn : "—")).ToList();
    }
}
