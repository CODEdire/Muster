using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Connectors;

/// <summary>
/// Reconciles a member's shadow wallet against an External/Hybrid currency's backing system: read the external
/// balance (from a credit/debit response if it returned one, else the GetBalance action) and post an adjusting
/// <see cref="CurrencyLedgerSource.Connector"/> ledger entry so the wallet matches. Connector-source entries aren't
/// pushed back out (loop guard) and don't cascade, so reconciliation is side-effect free beyond the wallet.
/// Used post-credit/debit, by the dashboard's on-visit/Sync action, by the admin "sync all", and by the sweep.
/// </summary>
public sealed class CurrencyConnectorSyncService(
    MusterDbContext db, ICurrencyConnectorClient client, ICurrencyService awards, ILogger<CurrencyConnectorSyncService> logger)
{
    /// <summary>The fixed throttle for dashboard on-visit syncs (a member landing on their wallets).</summary>
    public static readonly TimeSpan DashboardThrottle = TimeSpan.FromMinutes(5);

    /// <summary>Reconcile one member. <paramref name="knownBalance"/> short-circuits the GetBalance call when a
    /// credit/debit already returned the resulting balance. Returns the external balance, or null if unavailable.</summary>
    public async Task<long?> ReconcileAsync(ulong guildId, Guid currencyId, ulong userId, long? knownBalance, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyByIdAsync(guildId, currencyId, ct);
        if (currency is null || currency.Mode == CurrencyMode.Internal || !currency.Connector.Enabled)
        {
            return null;
        }

        var cfg = currency.Connector.Normalize();

        var external = knownBalance;
        if (external is null)
        {
            if (!cfg.GetBalance.Enabled)
            {
                return null; // nothing returned and no balance endpoint — can't reconcile
            }

            var name = await db.FindDisplayNameAsync(userId, ct);
            var probe = new ConnectorDispatch(guildId, currency.Code, userId, 0, "balance sync", "Connector", DateTimeOffset.UtcNow, 0, name);
            external = await client.GetBalanceAsync(cfg, probe, ct);
        }

        if (external is null)
        {
            return null;
        }

        // Adjust the shadow to match the external truth (no-op when already aligned).
        var shadow = await db.BalanceAsync(guildId, userId, currencyId, null, ct);
        var delta = external.Value - shadow;
        if (delta != 0)
        {
            await awards.AwardAsync(guildId, userId, currencyId, delta, CurrencyLedgerSource.Connector, sourceId: null, "External balance reconcile", ct);
        }

        // Stamp the sync time (the award above created/updated the wallet when delta != 0).
        var wallet = await db.FindWalletAsync(guildId, userId, currencyId, null, ct);
        if (wallet is not null)
        {
            wallet.LastSyncedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return external;
    }

    /// <summary>Reconcile one member (fetching the balance fresh).</summary>
    public Task<long?> SyncMemberAsync(ulong guildId, Guid currencyId, ulong userId, CancellationToken ct = default)
        => ReconcileAsync(guildId, currencyId, userId, knownBalance: null, ct);

    /// <summary>Whether a wallet is stale enough that a dashboard visit should trigger a background sync.</summary>
    public static bool IsDashboardSyncDue(DateTimeOffset? lastSyncedAt, DateTimeOffset now)
        => lastSyncedAt is null || now - lastSyncedAt >= DashboardThrottle;

    /// <summary>Reconcile a member's External/Hybrid balances (those with an enabled connector + GetBalance). On a
    /// dashboard visit pass <paramref name="force"/> = false to honour the 5-minute throttle; the Sync button passes
    /// true. Returns how many currencies were synced.</summary>
    public async Task<int> SyncMemberDueAsync(ulong guildId, ulong userId, bool force, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var synced = 0;
        foreach (var currency in await db.ListCurrenciesAsync(guildId, ct))
        {
            if (currency.Mode == CurrencyMode.Internal)
            {
                continue;
            }

            var cfg = currency.Connector.Normalize();
            if (!cfg.Enabled || !cfg.GetBalance.Enabled)
            {
                continue;
            }

            var wallet = await db.FindWalletAsync(guildId, userId, currency.Id, null, ct);
            if (wallet is null || (!force && !IsDashboardSyncDue(wallet.LastSyncedAt, now)))
            {
                continue;
            }

            if (await SyncMemberAsync(guildId, currency.Id, userId, ct) is not null)
            {
                synced++;
            }
        }

        return synced;
    }

    /// <summary>Reconcile every member holding a wallet for the currency, pacing calls by <paramref name="delay"/>
    /// (the external API is rate-limited and this can be slow for large guilds). Returns how many were synced.</summary>
    public async Task<int> SyncCurrencyAsync(ulong guildId, Guid currencyId, TimeSpan delay, IReadOnlyList<ulong>? userIds = null, CancellationToken ct = default)
    {
        var members = userIds ?? await db.ListWalletUserIdsAsync(guildId, currencyId, ct);
        var synced = 0;
        foreach (var userId in members)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await SyncMemberAsync(guildId, currencyId, userId, ct) is not null)
                {
                    synced++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Balance sync failed for user {UserId} (currency {CurrencyId})", userId, currencyId);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
        }

        logger.LogInformation("Synced {Count} member balance(s) for currency {CurrencyId}.", synced, currencyId);
        return synced;
    }
}
