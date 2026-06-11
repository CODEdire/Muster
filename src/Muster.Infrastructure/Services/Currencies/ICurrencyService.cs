using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Currencies;

/// <summary>Outcome of <see cref="ICurrencyService.RebuildWalletsAsync"/> — covered members/currencies, wallets
/// created, balances corrected.</summary>
public readonly record struct WalletRebuildResult(int Members, int Currencies, int WalletsCreated, int BalancesCorrected);

/// <summary>
/// The single money API. All currency movement — minting, spending, awards, and bounty escrow — flows
/// through one source of truth, which writes the ledger and publishes a <c>CurrencyMovementRecorded</c> message for
/// every leg. Callers depend on this interface, not the concrete service or any lower-level primitive.
/// </summary>
public interface ICurrencyService
{
    /// <summary>Current balance of a spendable currency (by code), or null if the currency is unknown.</summary>
    Task<long?> GetBalanceAsync(ulong guildId, string code, ulong userId, CancellationToken ct = default);

    /// <summary>Mint currency to a user (public API surface). No overdraft check — minting creates value.
    /// A non-null <paramref name="sourceId"/> makes it idempotent (deduped on (Connector, sourceId)).</summary>
    Task<CurrencyOperationResult> MintAsync(
        ulong guildId, string code, ulong userId, long amount, string reason, string? sourceId = null, CancellationToken ct = default);

    /// <summary>Spend currency from a user. Overdraft-checked when Muster owns the balance (Internal/Hybrid).
    /// A non-null <paramref name="sourceId"/> makes it idempotent (deduped on (Connector, sourceId)).</summary>
    Task<CurrencyOperationResult> SpendAsync(
        ulong guildId, string code, ulong userId, long amount, string reason, string? sourceId = null, CancellationToken ct = default);

    /// <summary>Move funds between two members (by code) in one transaction: debit the sender, credit the receiver.
    /// Overdraft-checked on the sender when Muster owns the balance (Internal/Hybrid). For External/Hybrid both legs
    /// push to the backing system (debit + credit) before commit. A non-null <paramref name="sourceId"/> makes the
    /// transfer idempotent (the two legs key off it with :out/:in suffixes). Returns the sender's resulting balance.</summary>
    Task<CurrencyOperationResult> TransferAsync(
        ulong guildId, string code, ulong fromUserId, ulong toUserId, long amount, string reason, string? sourceId = null, CancellationToken ct = default);

    /// <summary>Staff balance correction (by code): apply a signed delta. No overdraft check — staff is authoritative.
    /// For External/Hybrid it pushes Credit (delta &gt; 0) or Debit (delta &lt; 0) before commit. Idempotent on
    /// <paramref name="sourceId"/>. Returns the member's resulting balance.</summary>
    Task<CurrencyOperationResult> AdjustAsync(
        ulong guildId, string code, ulong userId, long delta, string reason, string? sourceId = null, CancellationToken ct = default);

    /// <summary>Award a currency (by id) and commit. Idempotent on (sourceType, sourceId).</summary>
    Task<CurrencyLedgerEntry> AwardAsync(
        ulong guildId, ulong userId, Guid currencyId, long amount,
        CurrencyLedgerSource sourceType, string? sourceId, string reason, CancellationToken ct = default);

    /// <summary>Award the guild's POINTS currency and commit.</summary>
    Task<CurrencyLedgerEntry> AwardPointsAsync(
        ulong guildId, ulong userId, long amount,
        CurrencyLedgerSource sourceType, string? sourceId, string reason, CancellationToken ct = default);

    /// <summary>Stage a POINTS award WITHOUT saving, so it commits in the caller's unit of work.</summary>
    Task StagePointsAsync(
        ulong guildId, ulong userId, long amount,
        CurrencyLedgerSource sourceType, string? sourceId, string reason, CancellationToken ct = default);

    /// <summary>Ensure every known member has a wallet for every currency, then recompute all balances from the
    /// ledger (the ledger is the source of truth, the wallet a cache). Returns how many members + currencies were
    /// covered, how many wallets were created, and how many balances corrected. Safe to run anytime.
    /// <para>NB: "members" is the local <c>GuildMembers</c> table — run a Discord roster sync first if it's stale,
    /// or only those members get wallets.</para></summary>
    Task<WalletRebuildResult> RebuildWalletsAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>Reserve the owner's funds into escrow (stages legs; caller commits). Validates spendable + funds.</summary>
    Task<EscrowStatus> HoldAsync(
        ulong guildId, ulong ownerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);

    /// <summary>Pay escrowed funds out to the completer (stages legs; caller commits).</summary>
    Task<EscrowStatus> PayoutAsync(
        ulong guildId, ulong completerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);

    /// <summary>Return escrowed funds to the owner (stages legs; caller commits).</summary>
    Task<EscrowStatus> RefundAsync(
        ulong guildId, ulong ownerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);

    /// <summary>Shop: reserve the buyer's funds into escrow (stages legs; caller commits). Validates spendable + funds.</summary>
    Task<EscrowStatus> ShopHoldAsync(
        ulong guildId, ulong buyerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);

    /// <summary>Shop: settle an order — pay <c>amount − fee</c> to the seller and burn <c>fee</c> to the sink
    /// (deflationary commission). Stages legs; caller commits.</summary>
    Task<EscrowStatus> ShopSettleAsync(
        ulong guildId, ulong sellerId, Guid currencyId, long amount, long fee, string sourceKey, CancellationToken ct = default);

    /// <summary>Shop: settle a guild-store sale by burning the escrowed amount (a coin sink — no member seller).
    /// Stages legs; caller commits.</summary>
    Task<EscrowStatus> ShopConsumeAsync(
        ulong guildId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);

    /// <summary>Shop: refund escrowed funds back to the buyer (stages legs; caller commits). No fee.</summary>
    Task<EscrowStatus> ShopRefundAsync(
        ulong guildId, ulong buyerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);

    /// <summary>Shop: charge a flat fee from a user and burn it to the sink (e.g. a featured-listing fee). Validates
    /// spendable + funds. Stages legs; caller commits.</summary>
    Task<EscrowStatus> ShopBurnAsync(
        ulong guildId, ulong userId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default);
}
