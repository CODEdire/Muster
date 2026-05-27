using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Currencies;

/// <summary>
/// A queued bulk currency adjustment: a staff mint/adjustment of one signed <see cref="Delta"/> applied to many
/// members (<see cref="TargetUserIds"/>) asynchronously. Persisted so the background worker can process it durably and
/// the UI can poll progress. The per-member ledger entries are idempotent (source key <c>bulk:{Id}:{userId}</c>), so a
/// redelivered job never double-applies; this row is also the single audited summary of the operation.
/// </summary>
public class CurrencyBulkBatch
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>The staff member who queued the batch.</summary>
    public ulong ActorId { get; set; }

    public Guid CurrencyId { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>Signed amount applied to each target (positive = mint, negative = deduction).</summary>
    public long Delta { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>The members the delta is applied to (resolved at queue time from a selection or a role).</summary>
    public List<ulong> TargetUserIds { get; set; } = [];

    public int TotalCount { get; set; }
    public int AppliedCount { get; set; }
    public int FailedCount { get; set; }

    public BulkBatchStatus Status { get; set; } = BulkBatchStatus.Pending;
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
