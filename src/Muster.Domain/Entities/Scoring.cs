using Muster.Domain.Enums;

namespace Muster.Domain.Entities;

/// <summary>A scoring period. Points leaderboards are scoped to a season; spendable currencies persist.</summary>
public class Season
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public SeasonStatus Status { get; set; } = SeasonStatus.Active;
}

/// <summary>
/// A guild-scoped currency. The seeded "POINTS" currency is seasonal and drives leaderboards;
/// spendable currencies (e.g. "COIN") persist as a wallet and are mintable/spendable by connectors.
/// </summary>
public class Currency
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Stable code, e.g. POINTS or COIN.</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public bool IsSeasonal { get; set; }
    public bool IsSpendable { get; set; }

    /// <summary>Where balance authority lives for this currency (see <see cref="CurrencyMode"/>).</summary>
    public CurrencyMode Mode { get; set; } = CurrencyMode.Internal;
}

/// <summary>
/// Append-only source of truth for balances and leaderboards. Every reward, spend, or adjustment is
/// a row here, tagged with its participation source for full audit.
/// </summary>
public class LedgerEntry
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    public Guid CurrencyId { get; set; }

    /// <summary>Null for non-seasonal (persistent) currencies.</summary>
    public Guid? SeasonId { get; set; }

    /// <summary>Signed amount: positive to award/mint, negative to spend.</summary>
    public long Amount { get; set; }

    public LedgerSourceType SourceType { get; set; }

    /// <summary>Identifier of the originating record (mission id, muster id, etc.).</summary>
    public string? SourceId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Cached balance per (guild,user,currency,season). Rebuildable from the ledger.</summary>
public class Wallet
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public Guid CurrencyId { get; set; }
    public Guid? SeasonId { get; set; }
    public long Balance { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
