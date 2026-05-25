using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Ledger;

public enum EscrowStatus
{
    Ok,
    CurrencyNotFound,
    NotSpendable,
    InsufficientFunds,
}

/// <summary>
/// Moves currency through a per-guild escrow account for player bounties. All methods STAGE ledger legs
/// without saving, so the caller commits them together with the bounty's state change in a single
/// transaction (durable: money never moves without the state advancing). Every leg is idempotent on a
/// distinct source key, so retries don't double-move. The escrow account is a sentinel "house" wallet
/// (<see cref="EscrowAccountUserId"/>) which keeps the ledger conserved.
/// </summary>
public class EscrowService(MusterDbContext db, AwardService awards)
{
    /// <summary>Sentinel user id representing the guild's escrow/house account.</summary>
    public const ulong EscrowAccountUserId = 0;

    /// <summary>Reserve the owner's funds into escrow. Validates spendable currency + sufficient balance.</summary>
    public async Task<EscrowStatus> HoldAsync(
        ulong guildId, ulong ownerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default)
    {
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.Id == currencyId && c.GuildId == guildId, ct);
        if (currency is null)
        {
            return EscrowStatus.CurrencyNotFound;
        }

        if (!currency.IsSpendable)
        {
            return EscrowStatus.NotSpendable;
        }

        if (await BalanceAsync(guildId, ownerId, currencyId, ct) < amount)
        {
            return EscrowStatus.InsufficientFunds;
        }

        await awards.StageAsync(guildId, ownerId, currencyId, -amount, LedgerSourceType.Mission, $"{sourceKey}:hold:owner", "Bounty escrow", ct);
        await awards.StageAsync(guildId, EscrowAccountUserId, currencyId, amount, LedgerSourceType.Mission, $"{sourceKey}:hold:escrow", "Bounty escrow", ct);
        return EscrowStatus.Ok;
    }

    /// <summary>Pay escrowed funds out to the completer.</summary>
    public async Task<EscrowStatus> PayoutAsync(
        ulong guildId, ulong completerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default)
    {
        await awards.StageAsync(guildId, EscrowAccountUserId, currencyId, -amount, LedgerSourceType.Mission, $"{sourceKey}:payout:escrow", "Bounty payout", ct);
        await awards.StageAsync(guildId, completerId, currencyId, amount, LedgerSourceType.Mission, $"{sourceKey}:payout:completer", "Bounty payout", ct);
        return EscrowStatus.Ok;
    }

    /// <summary>Return escrowed funds to the owner (reject / cancel / expire / abandon).</summary>
    public async Task<EscrowStatus> RefundAsync(
        ulong guildId, ulong ownerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default)
    {
        await awards.StageAsync(guildId, EscrowAccountUserId, currencyId, -amount, LedgerSourceType.Mission, $"{sourceKey}:refund:escrow", "Bounty refund", ct);
        await awards.StageAsync(guildId, ownerId, currencyId, amount, LedgerSourceType.Mission, $"{sourceKey}:refund:owner", "Bounty refund", ct);
        return EscrowStatus.Ok;
    }

    private async Task<long> BalanceAsync(ulong guildId, ulong userId, Guid currencyId, CancellationToken ct)
        => await db.LedgerEntries
            .Where(e => e.GuildId == guildId && e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == null)
            .SumAsync(e => (long?)e.Amount, ct) ?? 0;
}
