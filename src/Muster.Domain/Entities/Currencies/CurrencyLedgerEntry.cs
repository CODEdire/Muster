using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Currencies;

/// <summary>
/// Append-only source of truth for balances and leaderboards. Every reward, spend, or adjustment is
/// a row here, tagged with its participation source for full audit.
/// </summary>
public class CurrencyLedgerEntry
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    public Guid CurrencyId { get; set; }

    /// <summary>Null for non-seasonal (persistent) currencies.</summary>
    public Guid? SeasonId { get; set; }

    /// <summary>Signed amount: positive to award/mint, negative to spend.</summary>
    public long Amount { get; set; }

    public CurrencyLedgerSource SourceType { get; set; }

    /// <summary>Identifier of the originating record (mission id, muster id, etc.).</summary>
    public string? SourceId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public string Reason { get; set; } = string.Empty;
}
