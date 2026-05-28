using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Platform;

/// <summary>Platform-wide audit retention. <c>RetentionDays</c> ≤ 0 disables pruning (full history kept).
/// Default is 90 days — enough for typical security review windows without unbounded growth.</summary>
public class AuditRetentionOptions
{
    public int RetentionDays { get; set; } = 90;
}

/// <summary>Deletes audit rows older than <see cref="AuditRetentionOptions.RetentionDays"/>. Run periodically by
/// <see cref="AuditPruneScheduler"/> (or whatever host wants to schedule it). Idempotent and safe to call from a
/// single instance — concurrent runs do nothing harmful, just more cost.</summary>
public interface IAuditPruneService
{
    Task<int> PruneAsync(CancellationToken ct = default);
}

public class AuditPruneService(
    MusterDbContext db,
    IOptions<AuditRetentionOptions> options,
    ILogger<AuditPruneService> logger) : IAuditPruneService
{
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var days = options.Value.RetentionDays;
        if (days <= 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var deleted = await db.AuditLogs
            .Where(a => a.OccurredAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            logger.LogInformation("Pruned {Rows} audit row(s) older than {Days} days (cutoff {Cutoff:o}).",
                deleted, days, cutoff);
        }

        return deleted;
    }
}
