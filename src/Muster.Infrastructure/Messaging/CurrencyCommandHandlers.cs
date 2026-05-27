using Microsoft.Extensions.Logging;
using Muster.Contracts;
using Muster.Domain;
using Muster.Infrastructure.Connectors;
using Muster.Infrastructure.Services.Currencies;

namespace Muster.Infrastructure.Messaging;

// =====================================================================================================
// Currency Wolverine handlers. Two families share this file:
//   1. Machine-inbound mint/spend (connector mirror) — scope-gated API, CurrencyChangeResult, Connector source.
//   2. User/staff CQRS (transfer + staff adjust) — IGuildCommand → ICurrencyAuthorizer → Result (audited).
// Each handler is a plain static method: Wolverine injects services at runtime; unit tests call them directly.
// (Money-moved notifications are published as CurrencyMovementRecorded by CurrencyService itself, not here.)
// =====================================================================================================

// ---- 1. Machine inbound mint/spend (connector mirror) ----------------------------------------------

/// <summary>
/// Mint/spend invoked by an external economy mirroring a movement it already made. Scope-gated (not actor-
/// authorized), idempotent on <c>ExternalId</c>, written with <see cref="CurrencyLedgerSource.Connector"/> so it's
/// loop-guarded (never pushed back out). Returns <see cref="CurrencyChangeResult"/>.
/// </summary>
public static class MintCurrencyHandler
{
    public static async Task<CurrencyChangeResult> Handle(MintCurrency command, ICurrencyService currency, CancellationToken ct)
    {
        var result = await currency.MintAsync(
            command.GuildId, command.CurrencyCode, command.UserId, command.Amount, command.Reason, SourceId(command.ExternalId), ct);
        return ToResult(result);
    }

    internal static CurrencyChangeResult ToResult(CurrencyOperationResult result)
        => new(result.Status == CurrencyOperationStatus.Ok, result.Status.ToString(), result.Balance);

    /// <summary>Namespace an external delivery id into a ledger source key, so retried connector calls dedupe.</summary>
    internal static string? SourceId(string? externalId)
        => string.IsNullOrWhiteSpace(externalId) ? null : $"connector:{externalId}";
}

public static class SpendCurrencyHandler
{
    public static async Task<CurrencyChangeResult> Handle(SpendCurrency command, ICurrencyService currency, CancellationToken ct)
    {
        var result = await currency.SpendAsync(
            command.GuildId, command.CurrencyCode, command.UserId, command.Amount, command.Reason, MintCurrencyHandler.SourceId(command.ExternalId), ct);
        return MintCurrencyHandler.ToResult(result);
    }
}

// ---- 2. User/staff CQRS (authorized + audited) ------------------------------------------------------

/// <summary>
/// Move funds between members. Like the quest handlers, the single enforcement point: validate, authorize via
/// <see cref="ICurrencyAuthorizer"/> (members move only their own wallet; staff may move anyone's), then delegate
/// to <see cref="ICurrencyService"/>. Returns a <see cref="Result"/> so the audit middleware records it; the
/// resulting balance is read back by the adapter on success.
/// </summary>
public static class TransferCurrencyHandler
{
    public static async Task<Result> Handle(
        TransferCurrency command, ICurrencyAuthorizer authorizer, ICurrencyService currency, CancellationToken ct)
    {
        if (command.Amount <= 0)
        {
            return Result.Fail("Amount must be greater than zero.");
        }

        if (command.FromUserId == command.ToUserId)
        {
            return Result.Fail("You can't transfer to the same wallet.");
        }

        var actor = new GuildActor(command.GuildId, command.ActorId);
        if (!await authorizer.AuthorizeAsync(actor, command.FromUserId, CurrencyPermission.Transfer, ct))
        {
            return Result.Fail("Forbidden");
        }

        var code = (command.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var result = await currency.TransferAsync(
            command.GuildId, code, command.FromUserId, command.ToUserId, command.Amount, command.Reason, ct: ct);
        return ToResult(result);
    }

    internal static Result ToResult(CurrencyOperationResult result)
        => result.Status == CurrencyOperationStatus.Ok ? Result.Success() : Result.Fail(result.Status.ToString());
}

/// <summary>Staff balance correction / mint. A positive delta is a mint, negative a deduction — both staff-only.</summary>
public static class AdjustCurrencyHandler
{
    public static async Task<Result> Handle(
        AdjustCurrency command, ICurrencyAuthorizer authorizer, ICurrencyService currency, CancellationToken ct)
    {
        if (command.Delta == 0)
        {
            return Result.Fail("Amount must be non-zero.");
        }

        var permission = command.Delta > 0 ? CurrencyPermission.Mint : CurrencyPermission.Adjust;
        var actor = new GuildActor(command.GuildId, command.ActorId);
        if (!await authorizer.AuthorizeAsync(actor, command.UserId, permission, ct))
        {
            return Result.Fail("Forbidden");
        }

        var code = (command.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var result = await currency.AdjustAsync(
            command.GuildId, code, command.UserId, command.Delta, command.Reason, MintCurrencyHandler.SourceId(command.ExternalId), ct);
        return TransferCurrencyHandler.ToResult(result);
    }
}

// ---- 3. Background maintenance ----------------------------------------------------------------------

/// <summary>Admin-triggered "sync all members" — paced so it doesn't hammer the external API.</summary>
public static class SyncCurrencyBalancesHandler
{
    public static Task Handle(SyncCurrencyBalances command, CurrencyConnectorSyncService sync, CancellationToken ct)
        => sync.SyncCurrencyAsync(command.GuildId, command.CurrencyId, TimeSpan.FromMilliseconds(500), ct: ct);
}

/// <summary>
/// Applies a queued bulk adjustment durably: one <c>AdjustAsync</c> per target member, each keyed
/// <c>bulk:{batchId}:{userId}</c> so the ledger dedupes a redelivered job (and skips any duplicate connector push).
/// Progress + outcome are written back to the <see cref="Muster.Domain.Entities.Currencies.CurrencyBulkBatch"/> row
/// for the UI to poll. A member leg that throws (e.g. a connector failure) is counted as failed and the batch
/// continues; the run is marked Failed only if nothing applied.
/// </summary>
public static class RunCurrencyBulkAdjustHandler
{
    public static async Task Handle(
        RunCurrencyBulkAdjust command,
        Muster.Persistence.MusterDbContext db,
        ICurrencyService currency,
        Microsoft.Extensions.Logging.ILogger<RunCurrencyBulkAdjust> logger,
        CancellationToken ct)
    {
        var batch = await Muster.Persistence.Queries.CurrencyQueries.FindBulkBatchAsync(db, command.BatchId, ct);
        if (batch is null || batch.Status == Muster.Domain.Enums.BulkBatchStatus.Completed)
        {
            return; // unknown or already done — idempotent no-op on redelivery
        }

        batch.Status = Muster.Domain.Enums.BulkBatchStatus.Running;
        await db.SaveChangesAsync(ct);

        int applied = 0, failed = 0;
        foreach (var userId in batch.TargetUserIds)
        {
            try
            {
                // Idempotent per (Adjustment, sourceId): a rerun applies nothing new and skips the connector push.
                await currency.AdjustAsync(
                    batch.GuildId, batch.CurrencyCode, userId, batch.Delta, batch.Reason, $"bulk:{batch.Id}:{userId}", ct);
                applied++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Bulk adjust {Batch}: member {User} failed", batch.Id, userId);
            }
        }

        batch.AppliedCount = applied;
        batch.FailedCount = failed;
        batch.Status = applied == 0 && failed > 0 ? Muster.Domain.Enums.BulkBatchStatus.Failed : Muster.Domain.Enums.BulkBatchStatus.Completed;
        batch.Error = failed > 0 ? $"{failed:N0} member(s) failed." : null;
        batch.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
