namespace Muster.Contracts;

// Currency / ledger message contracts. Two families:
//   • machine-inbound mirror (MintCurrency/SpendCurrency) — broker-agnostic, return CurrencyChangeResult;
//   • user/staff CQRS (TransferCurrency/AdjustCurrency) — IGuildCommand, authorized + audited, return Result.
// In-app earning (quests/musters/events/tracking) awards directly through ICurrencyService; every staged movement
// is published as CurrencyMovementRecorded (the single "money moved" seam).

/// <summary>CQRS command to mint a currency (by code) to a member. Returns <see cref="CurrencyChangeResult"/>.
/// <paramref name="ExternalId"/> (optional) makes the mint idempotent: a connector that retries the same delivery
/// won't double-credit (deduped via the ledger's (SourceType, SourceId) unique key).
/// <paramref name="ActorId"/> is the admin actor responsible for this delivery — set from the API key's bound
/// actor; <c>0</c> reserved for internal/system mints (which currently call <c>ICurrencyService</c> directly,
/// not this command). The audit middleware records the actor on success.</summary>
public record MintCurrency(
    ulong GuildId, ulong ActorId, string CurrencyCode, ulong UserId, long Amount, string Reason, string? ExternalId = null) : IGuildCommand;

/// <summary>CQRS command to spend a currency (by code) from a member. Overdraft-checked per currency mode.
/// <paramref name="ExternalId"/> (optional) makes the spend idempotent for retried connector deliveries.
/// <paramref name="ActorId"/> is the admin actor responsible for this delivery (see <see cref="MintCurrency"/>).</summary>
public record SpendCurrency(
    ulong GuildId, ulong ActorId, string CurrencyCode, ulong UserId, long Amount, string Reason, string? ExternalId = null) : IGuildCommand;

/// <summary>Result of a mint/spend command. <see cref="Status"/> is a stable string (Ok / CurrencyNotFound / InsufficientFunds).</summary>
public record CurrencyChangeResult(bool Success, string Status, long Balance);

/// <summary>Reconcile every member's balance for an External/Hybrid currency against its backing system. Handled
/// in the background and paced (the external API is rate-limited), so it can take a while for large guilds.</summary>
public record SyncCurrencyBalances(ulong GuildId, Guid CurrencyId);

/// <summary>Run a queued bulk currency adjustment (staff mint/adjust applied to many members). Processed
/// asynchronously by the background worker; each member leg is idempotent (source key <c>bulk:{BatchId}:{userId}</c>),
/// so a redelivered job never double-applies. Progress is tracked on the <c>CurrencyBulkBatch</c> row.</summary>
public record RunCurrencyBulkAdjust(Guid BatchId);

/// <summary>
/// Published the instant a ledger movement is staged by <c>CurrencyService</c> (the single funnel), so it covers
/// <b>every</b> path — award, mint, spend, transfer, adjust, escrow. <see cref="Amount"/> is signed (negative =
/// debit); consumers filter for what they care about. The transactional-outbox successor to the old in-process
/// <c>ICurrencyEventSink</c>; a logging handler is the default seam until the external connector consumes it.
/// </summary>
public record CurrencyMovementRecorded(
    ulong GuildId,
    ulong UserId,
    Guid CurrencyId,
    long Amount,
    string SourceType,
    string? SourceId,
    string Reason,
    Guid? SeasonId);

/// <summary>Move spendable currency from one member's wallet to another. Members may transfer only out of their
/// own wallet (<see cref="ActorId"/> == <see cref="FromUserId"/>); economy staff may move anyone's. Overdraft-checked
/// when Muster owns the balance; for External/Hybrid both legs push to the backing system. Returns <see cref="Result"/>.</summary>
public record TransferCurrency(
    ulong GuildId, ulong ActorId, string CurrencyCode, ulong FromUserId, ulong ToUserId, long Amount, string Reason) : IGuildCommand;

/// <summary>Apply a signed balance correction to a member's wallet (staff). <see cref="Delta"/> &gt; 0 is a mint,
/// &lt; 0 a deduction; the handler authorizes Mint vs Adjust accordingly. For External/Hybrid it pushes the matching
/// Credit/Debit before commit. <paramref name="ExternalId"/> dedupes a retried API call. Returns <see cref="Result"/>.</summary>
public record AdjustCurrency(
    ulong GuildId, ulong ActorId, string CurrencyCode, ulong UserId, long Delta, string Reason, string? ExternalId = null) : IGuildCommand;
