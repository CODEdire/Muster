namespace Muster.Domain.Entities.Currencies;

/// <summary>
/// Per-(guild,user,currency,season) wallet. <see cref="Balance"/> is a cheap **display cache** kept in lock-step
/// with the ledger on every staged movement and rebuildable from it — convenient for fast reads (dashboard,
/// balance commands, leaderboards). It is NOT the transaction authority: spend/transfer overdraft decisions sum
/// the ledger (the source of truth), so a drifted cache can't enable an overdraft and self-heals on rebuild/sync.
/// </summary>
public class Wallet
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public Guid CurrencyId { get; set; }
    public Guid? SeasonId { get; set; }

    /// <summary>Cached balance for cheap display reads (see the type summary). Rebuildable from the ledger.</summary>
    public long Balance { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When this wallet was last reconciled against an external connector balance (null = never).
    /// Drives the dashboard 5-minute throttle and the periodic sweep's per-currency interval.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }
}
