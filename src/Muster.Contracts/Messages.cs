namespace Muster.Contracts;

// Broker-agnostic Wolverine message contracts. In v1 these flow in-process per container against the
// shared database + durable outbox. Enabling the Azure Service Bus transport later turns the
// bot -> web publishes into real cross-container delivery with no handler changes.

/// <summary>Raised by the bot when rewardable participation occurs; a handler writes the ledger.</summary>
public record MemberParticipated(
    ulong GuildId,
    ulong UserId,
    string SourceType,
    string SourceId,
    long SuggestedAmount,
    string Reason,
    DateTimeOffset OccurredAt);

/// <summary>Command to award currency to a member (manual award, mission approval, muster, etc.).</summary>
public record AwardCurrency(
    ulong GuildId,
    ulong UserId,
    Guid CurrencyId,
    long Amount,
    string SourceType,
    string? SourceId,
    string Reason);

/// <summary>Command issued by an external connector to mint or spend a spendable currency.</summary>
public record AdjustCurrencyBalance(
    ulong GuildId,
    ulong UserId,
    Guid CurrencyId,
    long Delta,
    string Reason);

/// <summary>CQRS command to mint a currency (by code) to a member. Returns <see cref="CurrencyChangeResult"/>.</summary>
public record MintCurrency(ulong GuildId, string CurrencyCode, ulong UserId, long Amount, string Reason);

/// <summary>CQRS command to spend a currency (by code) from a member. Overdraft-checked per currency mode.</summary>
public record SpendCurrency(ulong GuildId, string CurrencyCode, ulong UserId, long Amount, string Reason);

/// <summary>Result of a mint/spend command. <see cref="Status"/> is a stable string (Ok / CurrencyNotFound / InsufficientFunds).</summary>
public record CurrencyChangeResult(bool Success, string Status, long Balance);

/// <summary>Emitted after a ledger entry is committed; the hook outbound connectors subscribe to.</summary>
public record LedgerEntryRecorded(
    long LedgerEntryId,
    ulong GuildId,
    ulong UserId,
    Guid CurrencyId,
    long Amount,
    DateTimeOffset OccurredAt);

