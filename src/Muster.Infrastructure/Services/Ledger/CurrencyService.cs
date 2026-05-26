using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Ledger;

public enum EscrowStatus
{
    Ok,
    CurrencyNotFound,
    NotSpendable,
    InsufficientFunds,
}

public enum CurrencyOperationStatus
{
    Ok,
    CurrencyNotFound,
    InsufficientFunds,
}

public record CurrencyOperationResult(CurrencyOperationStatus Status, long Balance);

/// <summary>
/// The single source of truth for money. Owns the ledger write path (idempotent on (SourceType, SourceId),
/// season-scoped, wallet-consistent), the public mint/spend API, and bounty escrow. Every movement is emitted
/// as a <see cref="CurrencyMovement"/> the instant it's staged, so notifications around money live here rather
/// than scattered across the callers that happen to move it.
///
/// Spending is overdraft-checked when Muster is the balance authority (Internal/Hybrid); External-mode
/// currencies skip the check since the external system owns the balance and Muster's ledger is a shadow/audit
/// trail. External-currency escrow (an API/webhook hold) will later layer onto the same model via the outbox.
/// </summary>
public class CurrencyService(MusterDbContext db, ICurrencyEventSink events) : ICurrencyService
{
    /// <summary>Sentinel user id representing the guild's escrow/house account.</summary>
    public const ulong EscrowAccountUserId = 0;

    // ---------------------------------------------------------------------------------------------
    // Balance + public mint/spend API
    // ---------------------------------------------------------------------------------------------

    public async Task<long?> GetBalanceAsync(ulong guildId, string code, ulong userId, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return null;
        }

        Guid? seasonId = currency.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
        return await db.BalanceAsync(guildId, userId, currency.Id, seasonId, ct);
    }

    public async Task<CurrencyOperationResult> MintAsync(
        ulong guildId, string code, ulong userId, long amount, string reason, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.CurrencyNotFound, 0);
        }

        await AwardAsync(guildId, userId, currency.Id, amount, LedgerSourceType.Connector, sourceId: null, reason, ct);
        return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, userId, ct));
    }

    public async Task<CurrencyOperationResult> SpendAsync(
        ulong guildId, string code, ulong userId, long amount, string reason, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.CurrencyNotFound, 0);
        }

        // Only enforce the balance when Muster is the authority for it.
        if (currency.Mode != CurrencyMode.External)
        {
            Guid? seasonId = currency.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
            var balance = await db.BalanceAsync(guildId, userId, currency.Id, seasonId, ct);
            if (balance < amount)
            {
                return new CurrencyOperationResult(CurrencyOperationStatus.InsufficientFunds, balance);
            }
        }

        await AwardAsync(guildId, userId, currency.Id, -amount, LedgerSourceType.Connector, sourceId: null, reason, ct);
        return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, userId, ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Awards
    // ---------------------------------------------------------------------------------------------

    public async Task<LedgerEntry> AwardAsync(
        ulong guildId, ulong userId, Guid currencyId, long amount,
        LedgerSourceType sourceType, string? sourceId, string reason, CancellationToken ct = default)
    {
        var entry = await StageAsync(guildId, userId, currencyId, amount, sourceType, sourceId, reason, ct);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<LedgerEntry> AwardPointsAsync(
        ulong guildId, ulong userId, long amount,
        LedgerSourceType sourceType, string? sourceId, string reason, CancellationToken ct = default)
    {
        var points = await PointsAsync(guildId, ct);
        return await AwardAsync(guildId, userId, points.Id, amount, sourceType, sourceId, reason, ct);
    }

    public async Task StagePointsAsync(
        ulong guildId, ulong userId, long amount,
        LedgerSourceType sourceType, string? sourceId, string reason, CancellationToken ct = default)
    {
        var points = await PointsAsync(guildId, ct);
        await StageAsync(guildId, userId, points.Id, amount, sourceType, sourceId, reason, ct);
    }

    // ---------------------------------------------------------------------------------------------
    // Escrow (stage legs; caller commits with its state change in one transaction)
    // ---------------------------------------------------------------------------------------------

    public async Task<EscrowStatus> HoldAsync(
        ulong guildId, ulong ownerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyByIdAsync(guildId, currencyId, ct);
        if (currency is null)
        {
            return EscrowStatus.CurrencyNotFound;
        }

        if (!currency.IsSpendable)
        {
            return EscrowStatus.NotSpendable;
        }

        if (await db.BalanceAsync(guildId, ownerId, currencyId, null, ct) < amount)
        {
            return EscrowStatus.InsufficientFunds;
        }

        await StageAsync(guildId, ownerId, currencyId, -amount, LedgerSourceType.Quest, $"{sourceKey}:hold:owner", "Bounty escrow", ct);
        await StageAsync(guildId, EscrowAccountUserId, currencyId, amount, LedgerSourceType.Quest, $"{sourceKey}:hold:escrow", "Bounty escrow", ct);
        return EscrowStatus.Ok;
    }

    public async Task<EscrowStatus> PayoutAsync(
        ulong guildId, ulong completerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default)
    {
        await StageAsync(guildId, EscrowAccountUserId, currencyId, -amount, LedgerSourceType.Quest, $"{sourceKey}:payout:escrow", "Bounty payout", ct);
        await StageAsync(guildId, completerId, currencyId, amount, LedgerSourceType.Quest, $"{sourceKey}:payout:completer", "Bounty payout", ct);
        return EscrowStatus.Ok;
    }

    public async Task<EscrowStatus> RefundAsync(
        ulong guildId, ulong ownerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default)
    {
        await StageAsync(guildId, EscrowAccountUserId, currencyId, -amount, LedgerSourceType.Quest, $"{sourceKey}:refund:escrow", "Bounty refund", ct);
        await StageAsync(guildId, ownerId, currencyId, amount, LedgerSourceType.Quest, $"{sourceKey}:refund:owner", "Bounty refund", ct);
        return EscrowStatus.Ok;
    }

    // ---------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The one place a ledger entry is created. Stages the entry + wallet update WITHOUT saving (so callers
    /// can commit several legs with a state change in one transaction), and emits a <see cref="CurrencyMovement"/>
    /// for the new leg. Idempotent on (SourceType, SourceId): a duplicate returns the existing entry, stages
    /// nothing, and emits nothing (no movement happened).
    /// </summary>
    private async Task<LedgerEntry> StageAsync(
        ulong guildId, ulong userId, Guid currencyId, long amount,
        LedgerSourceType sourceType, string? sourceId, string reason, CancellationToken ct)
    {
        if (sourceId is not null)
        {
            var existing = await db.FindLedgerBySourceAsync(sourceType, sourceId, ct);
            if (existing is not null)
            {
                return existing;
            }
        }

        var currency = await db.FindCurrencyByIdAsync(guildId, currencyId, ct)
            ?? throw new InvalidOperationException($"Currency {currencyId} not found for guild {guildId}.");

        Guid? seasonId = null;
        if (currency.IsSeasonal)
        {
            seasonId = await db.ActiveSeasonIdAsync(guildId, ct);
        }

        var entry = new LedgerEntry
        {
            GuildId = guildId,
            UserId = userId,
            CurrencyId = currency.Id,
            SeasonId = seasonId,
            Amount = amount,
            SourceType = sourceType,
            SourceId = sourceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Reason = reason,
        };
        db.LedgerEntries.Add(entry);

        var wallet = await db.FindWalletAsync(guildId, userId, currency.Id, seasonId, ct);
        if (wallet is null)
        {
            wallet = new Wallet
            {
                GuildId = guildId,
                UserId = userId,
                CurrencyId = currency.Id,
                SeasonId = seasonId,
                Balance = 0,
            };
            db.Wallets.Add(wallet);
        }

        wallet.Balance += amount;
        wallet.UpdatedAt = DateTimeOffset.UtcNow;

        await events.OnMovementAsync(
            new CurrencyMovement(guildId, userId, currency.Id, amount, sourceType, sourceId, reason, seasonId), ct);

        return entry;
    }

    private async Task<Currency> PointsAsync(ulong guildId, CancellationToken ct)
        => await db.FindPointsAsync(guildId, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

    private async Task<long> CurrentBalanceAsync(ulong guildId, string code, ulong userId, CancellationToken ct)
        => await GetBalanceAsync(guildId, code, userId, ct) ?? 0;
}
