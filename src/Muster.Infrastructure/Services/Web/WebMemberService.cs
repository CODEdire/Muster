using Microsoft.EntityFrameworkCore;
using Muster.Domain.Enums;
using Muster.Infrastructure.Discord;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Tracking;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Web;

/// <summary>One ledger row for the wallet/points activity datagrid. <see cref="Source"/> is the typed enum so the
/// UI can render the right icon/label without re-parsing.</summary>
public record MemberLedgerRow(string Currency, long Amount, CurrencyLedgerSource Source, DateTimeOffset OccurredAt, string Reason);

public record MemberDetailView(
    ulong UserId,
    string DisplayName,
    IReadOnlyList<WalletBalance> Wallets,
    IReadOnlyList<MemberLedgerRow> History);

/// <summary>Header info for the MemberDetail page — identity strip + state badges.</summary>
public record MemberSummary(
    ulong UserId,
    string DisplayName,
    string? AvatarUrl,
    IReadOnlyList<string> RoleNames,
    TrackingChoice Tracking,
    bool DmOptOut,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LastActiveAt,
    bool IsBot);

/// <summary>One row of the admin member roster: a member and their balance in the currently-selected currency.</summary>
public record RosterRow(ulong UserId, string DisplayName, long Balance);

/// <summary>A page of the admin roster plus the total (filtered) count, for paging.</summary>
public record RosterPage(IReadOnlyList<RosterRow> Rows, int Total);

/// <summary>A guild role offered as a bulk target.</summary>
public record BulkRoleOption(ulong RoleId, string Name);

/// <summary>Top-of-page KPIs for the Members directory.</summary>
public record MembersKpis(int Total, int Active7d, int TrackingOptOuts, int NewLast30d);

/// <summary>One row in the Members directory datagrid (person-centric pivot — totals, status, last-active).</summary>
public record MembersDirectoryRow(
    ulong UserId,
    string DisplayName,
    string? AvatarUrl,
    int RoleCount,
    TrackingChoice Tracking,
    bool DmOptOut,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LastActiveAt,
    long Points,
    int CurrencyCount);

/// <summary>Read model for a member's profile/wallet page: balances plus ledger history (optionally one currency).
/// History + balances come from <see cref="ICurrencyReadService"/> so all surfaces share the same projection.</summary>
public class WebMemberService(MusterDbContext db, ICurrencyReadService scores)
{
    public async Task<MemberDetailView> GetAsync(
        ulong guildId, ulong userId, int historyCount = 50, string? currencyFilter = null, CancellationToken ct = default)
    {
        var wallets = await scores.GetWalletsAsync(guildId, userId, ct);

        var entries = await scores.GetMemberHistoryAsync(guildId, userId, currencyFilter, skip: 0, take: historyCount, ct);
        var history = entries
            .Select(e => new MemberLedgerRow(e.CurrencyCode, e.Amount, Enum.Parse<CurrencyLedgerSource>(e.SourceType), e.OccurredAt, e.Reason))
            .ToList();

        var member = await db.FindMemberAsync(guildId, userId, ct);
        var user = await db.FindUserAsync(userId, ct);
        var name = member?.Nickname ?? user?.GlobalName ?? user?.Username ?? userId.ToString();

        return new MemberDetailView(userId, name, wallets, history);
    }

    /// <summary>The member's own tracking-privacy choice on this guild (defaults to <see cref="TrackingChoice.Default"/>).</summary>
    public Task<TrackingChoice> GetTrackingChoiceAsync(ulong guildId, ulong userId, CancellationToken ct = default)
        => db.MemberTrackingChoiceAsync(guildId, userId, ct);

    /// <summary>Whether the user has opted out of currency receipt DMs (global, not per-guild).</summary>
    public Task<bool> GetCurrencyDmOptOutAsync(ulong userId, CancellationToken ct = default)
        => db.CurrencyDmOptOutAsync(userId, ct);

    /// <summary>The user's stored IANA time zone (null if they haven't set one — the guild zone is the fallback).</summary>
    public Task<string?> GetStoredZoneIdAsync(ulong userId, CancellationToken ct = default)
        => db.UserTimeZoneIdAsync(userId, ct);

    /// <summary>Other members a transfer can target: synced humans (no bots), excluding the sender, name-ordered.</summary>
    public async Task<IReadOnlyList<MemberOption>> GetRecipientsAsync(ulong guildId, ulong excludeUserId, CancellationToken ct = default)
    {
        var members = await db.ListMembersAsync(guildId, ct);
        var botIds = await db.BotUserIdsAsync(members.Select(m => m.UserId).ToList(), ct);
        members = members.Where(m => !botIds.Contains(m.UserId) && m.UserId != excludeUserId).ToList();

        var ids = members.Select(m => m.UserId).ToList();
        var names = await db.UserDisplayNameMapAsync(ids, ct);

        return members
            .Select(m => new MemberOption(m.UserId, m.Nickname ?? names.GetValueOrDefault(m.UserId, m.UserId.ToString())))
            .OrderBy(o => o.DisplayName)
            .ToList();
    }

    /// <summary>The admin member roster for one currency (by code): every synced human with their balance in that
    /// currency, name-filterable and paged (highest balance first). Empty when the currency is unknown.</summary>
    public async Task<RosterPage> GetRosterAsync(
        ulong guildId, string code, string? search, int skip, int take, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return new RosterPage([], 0);
        }

        Guid? seasonId = currency.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
        var humans = await HumanMembersAsync(guildId, ct);
        var names = await db.UserDisplayNameMapAsync(humans.Select(m => m.UserId).ToList(), ct);
        var balances = await db.WalletColumnAsync(guildId, currency.Id, seasonId, ct);

        IEnumerable<RosterRow> rows = humans
            .Select(m => new RosterRow(m.UserId, m.Nickname ?? names.GetValueOrDefault(m.UserId, m.UserId.ToString()), balances.GetValueOrDefault(m.UserId)));

        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = rows.Where(r => r.DisplayName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var ordered = rows.OrderByDescending(r => r.Balance).ThenBy(r => r.DisplayName).ToList();
        var page = ordered.Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, 200)).ToList();
        return new RosterPage(page, ordered.Count);
    }

    /// <summary>Synced human members of the guild holding the given role — the resolved target set for a role-scoped
    /// bulk action. Role membership is read from each member's synced role snapshot.</summary>
    public async Task<IReadOnlyList<ulong>> GetRoleTargetsAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
    {
        var humans = await HumanMembersAsync(guildId, ct);
        return humans.Where(m => m.RoleIds.Contains(roleId)).Select(m => m.UserId).ToList();
    }

    /// <summary>The guild's roles offered as bulk targets (name-ordered).</summary>
    public async Task<IReadOnlyList<BulkRoleOption>> GetRoleOptionsAsync(ulong guildId, CancellationToken ct = default)
    {
        var roles = await db.ListRolesAsync(guildId, ct);
        return roles.OrderBy(r => r.Name).Select(r => new BulkRoleOption(r.RoleId, r.Name)).ToList();
    }

    private async Task<List<Muster.Domain.Entities.Members.GuildMember>> HumanMembersAsync(ulong guildId, CancellationToken ct)
    {
        var members = await db.ListMembersAsync(guildId, ct);
        var botIds = await db.BotUserIdsAsync(members.Select(m => m.UserId).ToList(), ct);
        return members.Where(m => !botIds.Contains(m.UserId)).ToList();
    }

    /// <summary>Paged ledger history for one member across every currency (admin/self view — Points included).
    /// Reuses the wallet datagrid filters (search/range/sources/sort) so MemberDetail and Wallet share the same shape.</summary>
    public async Task<PagedResult<MemberLedgerRow>> GetHistoryPageAsync(
        ulong guildId, ulong userId, string? code, string? search, string sortKey, bool descending,
        int page, int pageSize, IReadOnlyCollection<CurrencyLedgerSource>? sources = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        Guid? currencyId = null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            var currency = await db.FindCurrencyAsync(guildId, code, ct);
            if (currency is null)
            {
                return new PagedResult<MemberLedgerRow>([], page, pageSize, 0);
            }

            currencyId = currency.Id;
        }

        var size = Math.Clamp(pageSize, 1, 100);
        var p = Math.Max(page, 1);
        var skip = (p - 1) * size;

        var (rows, total) = await db.MemberLedgerPagedAsync(
            guildId, userId, currencyId, search, sortKey, descending, skip, size, ct,
            sources: sources, from: from, to: to);

        var codes = await db.CurrencyCodeMapAsync(guildId, ct);
        var items = rows
            .Select(r => new MemberLedgerRow(codes.GetValueOrDefault(r.CurrencyId, "?"), r.Amount, r.SourceType, r.OccurredAt, r.Reason))
            .ToList();

        return new PagedResult<MemberLedgerRow>(items, p, size, total);
    }

    /// <summary>Header info for the MemberDetail page (identity + state badges + role names + last-active).
    /// Returns null for unknown members. Bots are returned but flagged so the UI can render appropriately.</summary>
    public async Task<MemberSummary?> GetSummaryAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (member is null)
        {
            return null;
        }

        var user = await db.FindUserAsync(userId, ct);
        var displayName = member.Nickname ?? user?.GlobalName ?? user?.Username ?? userId.ToString();
        var avatarUrl = DiscordCdn.AvatarUrl(userId, user?.AvatarHash);

        var roleNames = (await db.RoleNameMapAsync(guildId, member.RoleIds.ToList(), ct))
            .Values.OrderBy(n => n).ToList();

        var lastLedger = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && e.UserId == userId)
            .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct);

        var lastAttend = await (
                from a in db.VoiceAttendance
                join s in db.TrackingSessions on a.TrackingSessionId equals s.Id
                where s.GuildId == guildId && a.UserId == userId && a.LastSeenAt != null
                select a.LastSeenAt)
            .MaxAsync(ct);

        var lastActive = (lastLedger, lastAttend) switch
        {
            (null, null) => (DateTimeOffset?)null,
            ({ } l, null) => l,
            (null, { } a) => a,
            ({ } l, { } a) => l > a ? l : a,
        };

        return new MemberSummary(
            userId, displayName, avatarUrl, roleNames,
            member.Tracking, user?.CurrencyDmOptOut ?? false,
            member.JoinedAt, lastActive, user?.IsBot ?? false);
    }

    /// <summary>The Members directory: KPI strip + every human row (caller filters/sorts/pages in memory; the row
    /// set is bounded per-guild and we want one cached materialization so KPI counts and the visible page agree).</summary>
    public async Task<(MembersKpis Kpis, IReadOnlyList<MembersDirectoryRow> Rows)> LoadDirectoryAsync(
        ulong guildId, CancellationToken ct = default)
    {
        var members = await db.ListMembersAsync(guildId, ct);
        var ids = members.Select(m => m.UserId).ToList();
        var botIds = await db.BotUserIdsAsync(ids, ct);
        var humans = members.Where(m => !botIds.Contains(m.UserId)).ToList();
        var humanIds = humans.Select(m => m.UserId).ToList();

        if (humans.Count == 0)
        {
            return (new MembersKpis(0, 0, 0, 0), []);
        }

        var displays = await db.UserDisplayMapAsync(humanIds, ct);

        var dmOptOut = await db.Users
            .Where(u => humanIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.CurrencyDmOptOut, ct);

        var points = await db.FindPointsAsync(guildId, ct);
        Guid? pointsSeasonId = points?.IsSeasonal == true ? await db.ActiveSeasonIdAsync(guildId, ct) : null;

        Dictionary<ulong, long> pointsBalances = [];
        if (points is not null)
        {
            var pid = points.Id;
            pointsBalances = await db.Wallets
                .Where(w => w.GuildId == guildId && w.CurrencyId == pid && w.SeasonId == pointsSeasonId && humanIds.Contains(w.UserId))
                .ToDictionaryAsync(w => w.UserId, w => w.Balance, ct);
        }

        var currencyCountsQuery = db.Wallets
            .Where(w => w.GuildId == guildId && w.Balance != 0 && humanIds.Contains(w.UserId));
        if (points is not null)
        {
            var pid = points.Id;
            currencyCountsQuery = currencyCountsQuery.Where(w => w.CurrencyId != pid);
        }
        var currencyCounts = await currencyCountsQuery
            .GroupBy(w => w.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        var lastLedger = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && humanIds.Contains(e.UserId))
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, At = g.Max(e => e.OccurredAt) })
            .ToDictionaryAsync(x => x.UserId, x => x.At, ct);

        var lastAttend = await (
                from a in db.VoiceAttendance
                join s in db.TrackingSessions on a.TrackingSessionId equals s.Id
                where s.GuildId == guildId && humanIds.Contains(a.UserId) && a.LastSeenAt != null
                group a by a.UserId into g
                select new { UserId = g.Key, At = g.Max(a => a.LastSeenAt) })
            .ToDictionaryAsync(x => x.UserId, x => x.At, ct);

        var rows = humans.Select(m =>
        {
            var (name, avatarHash) = displays.GetValueOrDefault(m.UserId);
            DateTimeOffset? last = null;
            if (lastLedger.TryGetValue(m.UserId, out var lAt)) last = lAt;
            if (lastAttend.TryGetValue(m.UserId, out var aAt) && aAt is { } an && (last is null || an > last)) last = an;

            return new MembersDirectoryRow(
                m.UserId,
                m.Nickname ?? name ?? m.UserId.ToString(),
                DiscordCdn.AvatarUrl(m.UserId, avatarHash),
                m.RoleIds.Count,
                m.Tracking,
                dmOptOut.GetValueOrDefault(m.UserId),
                m.JoinedAt,
                last,
                pointsBalances.GetValueOrDefault(m.UserId),
                currencyCounts.GetValueOrDefault(m.UserId));
        }).ToList();

        var now = DateTimeOffset.UtcNow;
        var d7 = now.AddDays(-7);
        var d30 = now.AddDays(-30);
        var kpis = new MembersKpis(
            Total: rows.Count,
            Active7d: rows.Count(r => r.LastActiveAt is { } la && la >= d7),
            TrackingOptOuts: rows.Count(r => r.Tracking is TrackingChoice.BackgroundOut or TrackingChoice.AllOut),
            NewLast30d: rows.Count(r => r.JoinedAt >= d30));

        return (kpis, rows);
    }
}
