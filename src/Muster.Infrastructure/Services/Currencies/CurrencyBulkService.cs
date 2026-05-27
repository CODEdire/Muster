using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Wolverine;

namespace Muster.Infrastructure.Services.Currencies;

/// <summary>Outcome of queuing a bulk adjustment: the batch id to poll on success, or a reason it was rejected.</summary>
public record BulkQueueResult(bool Ok, string Message, Guid? BatchId = null);

/// <summary>Queues + reads staff bulk mint/adjust jobs. Callers depend on this interface.</summary>
public interface ICurrencyBulkService
{
    /// <summary>Authorize + persist a bulk adjustment and publish it for async processing.</summary>
    Task<BulkQueueResult> QueueAsync(ulong guildId, ulong actorId, string code, long delta, string reason, IReadOnlyCollection<ulong> userIds, CancellationToken ct = default);

    /// <summary>The batch's current progress (for the page's poll), or null if unknown.</summary>
    Task<CurrencyBulkBatch?> GetAsync(ulong guildId, Guid batchId, CancellationToken ct = default);
}

/// <summary>
/// Queues staff bulk mint/adjust (one signed delta applied to many members) for durable async processing. Queuing
/// authorizes once (mint/adjust is manager-only and subject-independent), persists a <see cref="CurrencyBulkBatch"/>
/// as the audited summary, then publishes <see cref="RunCurrencyBulkAdjust"/> so the background worker applies the
/// per-member legs (each idempotent). The actual ledger writes — and any per-member connector dispatch — happen in the
/// worker, so large/connector-backed batches never block the web request.
/// </summary>
public sealed class CurrencyBulkService(MusterDbContext db, ICurrencyAuthorizer authorizer, IMessageBus bus) : ICurrencyBulkService
{
    /// <summary>Hard cap on members per batch — keeps a single job bounded.</summary>
    public const int MaxTargets = 5000;

    public async Task<BulkQueueResult> QueueAsync(
        ulong guildId, ulong actorId, string code, long delta, string reason, IReadOnlyCollection<ulong> userIds, CancellationToken ct = default)
    {
        if (delta == 0)
        {
            return new BulkQueueResult(false, "Amount must be non-zero.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return new BulkQueueResult(false, "A reason is required.");
        }

        // Mint (delta > 0) / Adjust (delta < 0) are manager-only and don't depend on the subject, so authorize once.
        var permission = delta > 0 ? CurrencyPermission.Mint : CurrencyPermission.Adjust;
        if (!await authorizer.AuthorizeAsync(new GuildActor(guildId, actorId), actorId, permission, ct))
        {
            return new BulkQueueResult(false, "You're not allowed to do that.");
        }

        var normalized = code.Trim().ToUpperInvariant();
        var currency = await db.FindCurrencyAsync(guildId, normalized, ct);
        if (currency is null)
        {
            return new BulkQueueResult(false, "That currency doesn't exist here.");
        }

        // Clean the target set: distinct, real members only (never the escrow/house account).
        var targets = userIds.Where(id => id != CurrencyService.EscrowAccountUserId).Distinct().ToList();
        if (targets.Count == 0)
        {
            return new BulkQueueResult(false, "Select at least one member.");
        }

        if (targets.Count > MaxTargets)
        {
            return new BulkQueueResult(false, $"Too many members — limit is {MaxTargets:N0} per batch.");
        }

        var batch = new CurrencyBulkBatch
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            ActorId = actorId,
            CurrencyId = currency.Id,
            CurrencyCode = currency.Code,
            Delta = delta,
            Reason = reason.Trim(),
            TargetUserIds = targets,
            TotalCount = targets.Count,
            Status = BulkBatchStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CurrencyBulkBatches.Add(batch);

        var verb = delta > 0 ? "mint" : "adjust";
        db.AuditLogs.Add(new AuditLog
        {
            GuildId = guildId,
            ActorUserId = actorId,
            Action = $"currency.bulk{(delta > 0 ? "Mint" : "Adjust")}",
            Details = $"Bulk {verb} {delta:+#,##0;-#,##0} {currency.Code} to {targets.Count:N0} member(s): {batch.Reason}",
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(new RunCurrencyBulkAdjust(batch.Id));

        return new BulkQueueResult(true, $"Queued — applying to {targets.Count:N0} member(s).", batch.Id);
    }

    /// <summary>The batch's current progress (for the page's poll), or null if unknown.</summary>
    public Task<CurrencyBulkBatch?> GetAsync(ulong guildId, Guid batchId, CancellationToken ct = default)
        => db.FindBulkBatchAsync(guildId, batchId, ct);
}
