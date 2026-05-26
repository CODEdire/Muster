using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Ledger;
namespace Muster.Infrastructure.Services.Web;

public record MemberLedgerRow(string Currency, long Amount, string Source, DateTimeOffset OccurredAt, string Reason);

public record MemberDetailView(
    ulong UserId,
    string DisplayName,
    IReadOnlyList<WalletBalance> Wallets,
    IReadOnlyList<MemberLedgerRow> History);

/// <summary>Read model for a member's profile/detail page: balances plus recent ledger history.</summary>
public class WebMemberService(MusterDbContext db, ScoreQueryService scores)
{
    public async Task<MemberDetailView> GetAsync(ulong guildId, ulong userId, int historyCount = 50, CancellationToken ct = default)
    {
        var wallets = await scores.GetWalletsAsync(guildId, userId, ct);

        var currencyCodes = await db.CurrencyCodeMapAsync(guildId, ct);

        var entries = await db.RecentHistoryAsync(guildId, userId, historyCount, ct);

        var history = entries
            .Select(e => new MemberLedgerRow(
                currencyCodes.GetValueOrDefault(e.CurrencyId, "?"), e.Amount, e.SourceType.ToString(), e.OccurredAt, e.Reason))
            .ToList();

        var member = await db.FindMemberAsync(guildId, userId, ct);
        var user = await db.FindUserAsync(userId, ct);
        var name = member?.Nickname ?? user?.GlobalName ?? user?.Username ?? userId.ToString();

        return new MemberDetailView(userId, name, wallets, history);
    }
}
