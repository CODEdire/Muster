using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Connectors;
using Wolverine;

namespace Muster.Infrastructure.Services.Currencies;

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
/// season-scoped, wallet-consistent), the public mint/spend API, and bounty escrow. Every movement is published
/// as a <see cref="CurrencyMovementRecorded"/> message the instant it's staged, so notifications around money live
/// here rather than scattered across the callers that happen to move it.
///
/// Spending is overdraft-checked when Muster is the balance authority (Internal/Hybrid); External-mode
/// currencies skip the check since the external system owns the balance and Muster's ledger is a shadow/audit
/// trail. External-currency escrow (an API/webhook hold) will later layer onto the same model via the outbox.
/// </summary>
public class CurrencyService(MusterDbContext db, IMessageBus bus, ICurrencyConnectorClient? connector = null) : ICurrencyService
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
        ulong guildId, string code, ulong userId, long amount, string reason, string? sourceId = null, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.CurrencyNotFound, 0);
        }

        await AwardAsync(guildId, userId, currency.Id, amount, CurrencyLedgerSource.Connector, sourceId, reason, ct);
        return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, userId, ct));
    }

    public async Task<CurrencyOperationResult> SpendAsync(
        ulong guildId, string code, ulong userId, long amount, string reason, string? sourceId = null, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.CurrencyNotFound, 0);
        }

        // A retried connector delivery (same sourceId) must not double-spend: if it already applied, return Ok
        // with the current balance and stage nothing (AwardAsync is idempotent, but short-circuit so the
        // overdraft check below doesn't reject a now-lower balance on the retry).
        if (sourceId is not null && await db.FindLedgerBySourceAsync(CurrencyLedgerSource.Connector, sourceId, ct) is not null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, userId, ct));
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

        await AwardAsync(guildId, userId, currency.Id, -amount, CurrencyLedgerSource.Connector, sourceId, reason, ct);
        return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, userId, ct));
    }

    public async Task<CurrencyOperationResult> TransferAsync(
        ulong guildId, string code, ulong fromUserId, ulong toUserId, long amount, string reason, string? sourceId = null, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.CurrencyNotFound, 0);
        }

        // A retried transfer (same sourceId) must not move funds twice: if the debit leg already exists, no-op.
        var outKey = sourceId is null ? null : $"{sourceId}:out";
        var inKey = sourceId is null ? null : $"{sourceId}:in";
        if (outKey is not null && await db.FindLedgerBySourceAsync(CurrencyLedgerSource.Transfer, outKey, ct) is not null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, fromUserId, ct));
        }

        // Overdraft-check the sender when Muster is the authority for this currency.
        if (currency.Mode != CurrencyMode.External)
        {
            Guid? seasonId = currency.IsSeasonal ? await db.ActiveSeasonIdAsync(guildId, ct) : null;
            var balance = await db.BalanceAsync(guildId, fromUserId, currency.Id, seasonId, ct);
            if (balance < amount)
            {
                return new CurrencyOperationResult(CurrencyOperationStatus.InsufficientFunds, balance);
            }
        }

        // Two legs in one unit of work. Each leg's StageAsync pushes its external Debit/Credit first (External/Hybrid),
        // so a failed push aborts the whole transfer before commit.
        await StageAsync(guildId, fromUserId, currency.Id, -amount, CurrencyLedgerSource.Transfer, outKey, reason, ct);
        await StageAsync(guildId, toUserId, currency.Id, amount, CurrencyLedgerSource.Transfer, inKey, reason, ct);
        await db.SaveChangesAsync(ct);
        return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, fromUserId, ct));
    }

    public async Task<CurrencyOperationResult> AdjustAsync(
        ulong guildId, string code, ulong userId, long delta, string reason, string? sourceId = null, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyAsync(guildId, code, ct);
        if (currency is null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.CurrencyNotFound, 0);
        }

        await AwardAsync(guildId, userId, currency.Id, delta, CurrencyLedgerSource.Adjustment, sourceId, reason, ct);
        return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, userId, ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Awards
    // ---------------------------------------------------------------------------------------------

    public async Task<CurrencyLedgerEntry> AwardAsync(
        ulong guildId, ulong userId, Guid currencyId, long amount,
        CurrencyLedgerSource sourceType, string? sourceId, string reason, CancellationToken ct = default)
    {
        var entry = await StageAsync(guildId, userId, currencyId, amount, sourceType, sourceId, reason, ct);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<CurrencyLedgerEntry> AwardPointsAsync(
        ulong guildId, ulong userId, long amount,
        CurrencyLedgerSource sourceType, string? sourceId, string reason, CancellationToken ct = default)
    {
        var points = await PointsAsync(guildId, ct);
        return await AwardAsync(guildId, userId, points.Id, amount, sourceType, sourceId, reason, ct);
    }

    public async Task StagePointsAsync(
        ulong guildId, ulong userId, long amount,
        CurrencyLedgerSource sourceType, string? sourceId, string reason, CancellationToken ct = default)
    {
        var points = await PointsAsync(guildId, ct);
        await StageAsync(guildId, userId, points.Id, amount, sourceType, sourceId, reason, ct);
    }

    // ---------------------------------------------------------------------------------------------
    // Maintenance
    // ---------------------------------------------------------------------------------------------

    public async Task<int> RebuildWalletsAsync(ulong guildId, CancellationToken ct = default)
    {
        // The ledger is the source of truth; recompute each (user,currency,season) balance from it.
        var sums = await db.LedgerTotalsAsync(guildId, ct);

        var wallets = await db.WalletsForGuildAsync(guildId, ct);
        var byKey = wallets.ToDictionary(w => (w.UserId, w.CurrencyId, w.SeasonId));
        var ledgerKeys = sums.Select(s => (s.UserId, s.CurrencyId, s.SeasonId)).ToHashSet();
        var now = DateTimeOffset.UtcNow;
        var changed = 0;

        foreach (var s in sums)
        {
            if (!byKey.TryGetValue((s.UserId, s.CurrencyId, s.SeasonId), out var wallet))
            {
                wallet = new Wallet { GuildId = guildId, UserId = s.UserId, CurrencyId = s.CurrencyId, SeasonId = s.SeasonId };
                db.Wallets.Add(wallet);
            }

            if (wallet.Balance != s.Total)
            {
                wallet.Balance = s.Total;
                wallet.UpdatedAt = now;
                changed++;
            }
        }

        // A wallet with no ledger rows behind it has drifted — correct it to zero.
        foreach (var wallet in wallets.Where(w => !ledgerKeys.Contains((w.UserId, w.CurrencyId, w.SeasonId)) && w.Balance != 0))
        {
            wallet.Balance = 0;
            wallet.UpdatedAt = now;
            changed++;
        }

        await db.SaveChangesAsync(ct);
        return changed;
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

        await StageAsync(guildId, ownerId, currencyId, -amount, CurrencyLedgerSource.Quest, $"{sourceKey}:hold:owner", "Bounty escrow", ct);
        await StageAsync(guildId, EscrowAccountUserId, currencyId, amount, CurrencyLedgerSource.Quest, $"{sourceKey}:hold:escrow", "Bounty escrow", ct);
        return EscrowStatus.Ok;
    }

    public async Task<EscrowStatus> PayoutAsync(
        ulong guildId, ulong completerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default)
    {
        await StageAsync(guildId, EscrowAccountUserId, currencyId, -amount, CurrencyLedgerSource.Quest, $"{sourceKey}:payout:escrow", "Bounty payout", ct);
        await StageAsync(guildId, completerId, currencyId, amount, CurrencyLedgerSource.Quest, $"{sourceKey}:payout:completer", "Bounty payout", ct);
        return EscrowStatus.Ok;
    }

    public async Task<EscrowStatus> RefundAsync(
        ulong guildId, ulong ownerId, Guid currencyId, long amount, string sourceKey, CancellationToken ct = default)
    {
        await StageAsync(guildId, EscrowAccountUserId, currencyId, -amount, CurrencyLedgerSource.Quest, $"{sourceKey}:refund:escrow", "Bounty refund", ct);
        await StageAsync(guildId, ownerId, currencyId, amount, CurrencyLedgerSource.Quest, $"{sourceKey}:refund:owner", "Bounty refund", ct);
        return EscrowStatus.Ok;
    }

    // ---------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The one place a ledger entry is created. Stages the entry + wallet update WITHOUT saving (so callers
    /// can commit several legs with a state change in one transaction), and publishes a <c>CurrencyMovementRecorded</c>
    /// for the new leg. Idempotent on (SourceType, SourceId): a duplicate returns the existing entry, stages
    /// nothing, and publishes nothing (no movement happened).
    /// </summary>
    private async Task<CurrencyLedgerEntry> StageAsync(
        ulong guildId, ulong userId, Guid currencyId, long amount,
        CurrencyLedgerSource sourceType, string? sourceId, string reason, CancellationToken ct)
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

        // External-before-finalize: push the movement to the backing economy and require success BEFORE we stage
        // the ledger leg. A failed/timed-out external call throws, rolling the whole operation back (incl. any
        // quest payout that drove it) since nothing is saved yet. Connector-origin movements skip this (loop guard).
        await EnsureExternalAsync(currency, userId, amount, reason, sourceType, sourceId, ct);

        Guid? seasonId = null;
        if (currency.IsSeasonal)
        {
            seasonId = await db.ActiveSeasonIdAsync(guildId, ct);
        }

        var entry = new CurrencyLedgerEntry
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
        db.CurrencyLedgerEntries.Add(entry);

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

        // Publish the movement (transactional outbox enlists it with the caller's commit when run in a handler).
        await bus.PublishAsync(
            new CurrencyMovementRecorded(guildId, userId, currency.Id, amount, sourceType.ToString(), sourceId, reason, seasonId));

        return entry;
    }

    /// <summary>
    /// For External/Hybrid currencies with an enabled connector, run the Credit (amount &gt; 0) or Debit
    /// (amount &lt; 0) action and require it to succeed. Throws <see cref="ConnectorException"/> on a real failure so
    /// the caller's transaction rolls back. Skipped (no-op) when: no connector wired in DI, the gate says don't
    /// dispatch (Internal mode / disabled / connector-origin), the escrow/house account, a zero movement, or the
    /// action simply isn't configured (nothing to push).
    /// </summary>
    private async Task EnsureExternalAsync(
        Currency currency, ulong userId, long amount, string reason, CurrencyLedgerSource sourceType, string? sourceId, CancellationToken ct)
    {
        if (connector is null
            || amount == 0
            || userId == EscrowAccountUserId
            || !CurrencyConnectorClient.ShouldDispatch(currency.Mode, currency.Connector, sourceType))
        {
            return;
        }

        var cfg = currency.Connector.Normalize();
        var kind = amount > 0 ? ConnectorActionKind.Credit : ConnectorActionKind.Debit;

        var name = await db.FindDisplayNameAsync(userId, ct);
        var deliveryId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dispatch = new ConnectorDispatch(
            currency.GuildId, currency.Code, userId, amount, reason, sourceType.ToString(), DateTimeOffset.UtcNow, deliveryId, name,
            IdempotencyKey: sourceId); // stable across retries so a resilience retry can't double-apply remotely

        var result = await connector.ExecuteAsync(cfg, kind, dispatch, ct);

        // "Not configured" means there's no endpoint for this direction — treat as nothing-to-push, not an abort.
        if (!result.Configured)
        {
            return;
        }

        CurrencyConnectorClient.RecordHealth(cfg, result);
        if (!result.Success)
        {
            throw new ConnectorException(
                $"External {kind} for {currency.Code} failed: {result.Error ?? "unknown error"}. Operation aborted.");
        }
    }

    private async Task<Currency> PointsAsync(ulong guildId, CancellationToken ct)
        => await db.FindPointsAsync(guildId, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

    private async Task<long> CurrentBalanceAsync(ulong guildId, string code, ulong userId, CancellationToken ct)
        => await GetBalanceAsync(guildId, code, userId, ct) ?? 0;
}
