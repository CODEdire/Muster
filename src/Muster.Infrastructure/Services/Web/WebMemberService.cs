using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Currencies;
namespace Muster.Infrastructure.Services.Web;

public record MemberLedgerRow(string Currency, long Amount, string Source, DateTimeOffset OccurredAt, string Reason);

public record MemberDetailView(
    ulong UserId,
    string DisplayName,
    IReadOnlyList<WalletBalance> Wallets,
    IReadOnlyList<MemberLedgerRow> History);

/// <summary>One row of the admin member roster: a member and their balance in the currently-selected currency.</summary>
public record RosterRow(ulong UserId, string DisplayName, long Balance);

/// <summary>A page of the admin roster plus the total (filtered) count, for paging.</summary>
public record RosterPage(IReadOnlyList<RosterRow> Rows, int Total);

/// <summary>A guild role offered as a bulk target.</summary>
public record BulkRoleOption(ulong RoleId, string Name);

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
            .Select(e => new MemberLedgerRow(e.CurrencyCode, e.Amount, e.SourceType, e.OccurredAt, e.Reason))
            .ToList();

        var member = await db.FindMemberAsync(guildId, userId, ct);
        var user = await db.FindUserAsync(userId, ct);
        var name = member?.Nickname ?? user?.GlobalName ?? user?.Username ?? userId.ToString();

        return new MemberDetailView(userId, name, wallets, history);
    }

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
}
