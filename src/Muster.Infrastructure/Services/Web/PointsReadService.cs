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

    /// <summary>Paged top holders of POINTS (escrow excluded).</summary>
    public async Task<PagedResult<LeaderboardRow>> GetTopHoldersPageAsync(
        ulong guildId, int page, int pageSize, CancellationToken ct = default)
    {
        var points = await db.FindPointsAsync(guildId, ct);
        if (points is null)
        {
            return new PagedResult<LeaderboardRow>([], page, pageSize, 0);
        }

        Guid? seasonId = points.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
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
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
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
            sources: sources, from: from, to: to);

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
}
