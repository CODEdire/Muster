using Microsoft.EntityFrameworkCore;

using Muster.Infrastructure.Persistence;
namespace Muster.Infrastructure.Services;

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

        var currencyCodes = await db.Currencies
            .Where(c => c.GuildId == guildId)
            .ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        var entries = await db.LedgerEntries
            .Where(e => e.GuildId == guildId && e.UserId == userId)
            .OrderByDescending(e => e.Id)
            .Take(historyCount)
            .Select(e => new { e.CurrencyId, e.Amount, e.SourceType, e.OccurredAt, e.Reason })
            .ToListAsync(ct);

        var history = entries
            .Select(e => new MemberLedgerRow(
                currencyCodes.GetValueOrDefault(e.CurrencyId, "?"), e.Amount, e.SourceType.ToString(), e.OccurredAt, e.Reason))
            .ToList();

        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        var name = member?.Nickname ?? user?.GlobalName ?? user?.Username ?? userId.ToString();

        return new MemberDetailView(userId, name, wallets, history);
    }
}
