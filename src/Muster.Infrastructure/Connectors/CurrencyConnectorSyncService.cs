using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Platform;
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
    MusterDbContext db, ICurrencyConnectorClient client, ICurrencyService awards,
    ILogger<CurrencyConnectorSyncService> logger, AuditService? audit = null)
{
    /// <summary>The fixed throttle for dashboard on-visit syncs (a member landing on their wallets).</summary>
    public static readonly TimeSpan DashboardThrottle = TimeSpan.FromMinutes(5);

    /// <summary>A reconcile correction at or above this magnitude is audited as an anomaly (the per-member reconcile
    /// is otherwise ledger-only). Large unexplained drift = possible external tampering or integration bug.</summary>
    public const long DriftAnomalyThreshold = 1000;

    /// <summary>Outcome counts for one currency's reconcile pass — drives the sweep-summary audit.</summary>
    public readonly record struct CurrencySyncStats(int Candidates, int Synced, int Failed);

    /// <summary>Sync progress for a currency: total members, how many have a synced wallet, the outstanding backlog,
    /// and the oldest/newest sync stamps.</summary>
    public readonly record struct CurrencySyncBacklog(int Members, int Synced, int Pending, DateTimeOffset? OldestSync, DateTimeOffset? NewestSync);

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

            // Per-member reconcile is ledger-only by design, but flag an anomalously large correction for an auditor.
            if (audit is not null && Math.Abs(delta) >= DriftAnomalyThreshold)
            {
                await audit.RecordAsync(
                    guildId, CurrencyService.EscrowAccountUserId, AuditActions.Currency.DriftAnomaly,
                    new CurrencyDriftAnomalyPayload(currency.Code, userId, delta, external.Value),
                    origin: AuditOrigin.Connector, outcome: AuditOutcome.Warning, targetUserId: userId, ct: ct);
            }
        }

        // Stamp the sync time. A non-zero delta already created/updated the wallet via the award; when the delta is
        // zero (external already matches), there may be no wallet row yet — create one so a synced member always has
        // a wallet (and a LastSyncedAt to throttle future syncs), rather than re-syncing on every visit.
        var wallet = await db.FindWalletAsync(guildId, userId, currencyId, null, ct);
        if (wallet is null)
        {
            wallet = new Wallet { GuildId = guildId, UserId = userId, CurrencyId = currencyId, SeasonId = null, Balance = external.Value };
            db.Wallets.Add(wallet);
        }

        wallet.LastSyncedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

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

            // Sync when forced (the Sync button), when there's no local wallet yet (the first pull from the external
            // system — this is exactly when we need to fetch + create it), or when an existing wallet is stale enough
            // for a dashboard auto-sync. Previously a null wallet was skipped, so a member's external balance was
            // never pulled until they happened to get a local wallet some other way.
            var wallet = await db.FindWalletAsync(guildId, userId, currency.Id, null, ct);
            if (!force && wallet is not null && !IsDashboardSyncDue(wallet.LastSyncedAt, now))
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

    /// <summary>A snapshot of how far the per-member balance sync has progressed for a currency: total guild members,
    /// how many already have a synced wallet, and the oldest sync stamp. The "backlog" is <c>Members − Synced</c> — the
    /// members the next full sync still has to reconcile.</summary>
    public async Task<CurrencySyncBacklog> SyncBacklogAsync(ulong guildId, Guid currencyId, CancellationToken ct = default)
    {
        var members = await db.GuildMembers.CountAsync(m => m.GuildId == guildId, ct);
        var walletQuery = db.Wallets.Where(w => w.GuildId == guildId && w.CurrencyId == currencyId);
        var synced = await walletQuery.CountAsync(w => w.LastSyncedAt != null, ct);
        var oldest = await walletQuery.Where(w => w.LastSyncedAt != null).MinAsync(w => (DateTimeOffset?)w.LastSyncedAt, ct);
        var newest = await walletQuery.MaxAsync(w => (DateTimeOffset?)w.LastSyncedAt, ct);
        return new CurrencySyncBacklog(members, synced, Math.Max(0, members - synced), oldest, newest);
    }

    /// <summary>Reconcile the currency's balances, pacing calls by <paramref name="delay"/> (the external API is
    /// rate-limited and this can be slow for large guilds). Returns per-member outcome counts. When
    /// <paramref name="userIds"/> is null this syncs <b>every guild member</b> (not just current wallet-holders), so a
    /// member with an external balance but no local wallet yet is pulled in and their wallet created.</summary>
    public async Task<CurrencySyncStats> SyncCurrencyAsync(ulong guildId, Guid currencyId, TimeSpan delay, IReadOnlyList<ulong>? userIds = null, CancellationToken ct = default)
    {
        var members = userIds ?? (await db.ListMembersAsync(guildId, ct)).Select(m => m.UserId).ToList();
        var synced = 0;
        var failed = 0;
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
                failed++;
                logger.LogWarning(ex, "Balance sync failed for user {UserId} (currency {CurrencyId})", userId, currencyId);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
        }

        logger.LogInformation("Synced {Count} member balance(s) for currency {CurrencyId} ({Failed} failed).", synced, currencyId, failed);
        return new CurrencySyncStats(members.Count, synced, failed);
    }
}
