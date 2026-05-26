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
}
