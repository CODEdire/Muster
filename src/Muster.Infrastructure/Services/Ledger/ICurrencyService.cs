using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Ledger;

/// <summary>
/// The single money API. All currency movement — minting, spending, awards, and bounty escrow — flows
/// through one source of truth, which writes the ledger and emits a <see cref="CurrencyMovement"/> for
/// every leg. Callers depend on this interface, not the concrete service or any lower-level primitive.
/// </summary>
public interface ICurrencyService
{
    /// <summary>Current balance of a spendable currency (by code), or null if the currency is unknown.</summary>
    Task<long?> GetBalanceAsync(ulong guildId, string code, ulong userId, CancellationToken ct = default);

    /// <summary>Mint currency to a user (public API surface). No overdraft check — minting creates value.</summary>
    Task<CurrencyOperationResult> MintAsync(
        ulong guildId, string code, ulong userId, long amount, string reason, CancellationToken ct = default);

    /// <summary>Spend currency from a user. Overdraft-checked when Muster owns the balance (Internal/Hybrid).</summary>
    Task<CurrencyOperationResult> SpendAsync(
        ulong guildId, string code, ulong userId, long amount, string reason, CancellationToken ct = default);

    /// <summary>Award a currency (by id) and commit. Idempotent on (sourceType, sourceId).</summary>
    Task<LedgerEntry> AwardAsync(
        ulong guildId, ulong userId, Guid currencyId, long amount,
        LedgerSourceType sourceType, string? sourceId, string reason, CancellationToken ct = default);

    /// <summary>Award the guild's POINTS currency and commit.</summary>
    Task<LedgerEntry> AwardPointsAsync(
        ulong guildId, ulong userId, long amount,
        LedgerSourceType sourceType, string? sourceId, string reason, CancellationToken ct = default);

    /// <summary>Stage a POINTS award WITHOUT saving, so it commits in the caller's unit of work.</summary>
    Task StagePointsAsync(
        ulong guildId, ulong userId, long amount,
        LedgerSourceType sourceType, string? sourceId, string reason, CancellationToken ct = default);

    /// <summary>Reserve the owner's funds into escrow (stages legs; caller commits). Validates spendable + funds.</summary>
    Task<EscrowStatus> HoldAsync(
        ulong guildId, ulong ownerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);

    /// <summary>Pay escrowed funds out to the completer (stages legs; caller commits).</summary>
    Task<EscrowStatus> PayoutAsync(
        ulong guildId, ulong completerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);

    /// <summary>Return escrowed funds to the owner (stages legs; caller commits).</summary>
    Task<EscrowStatus> RefundAsync(
        ulong guildId, ulong ownerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);
}
