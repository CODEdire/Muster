using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Currencies;

/// <summary>
/// Compacts ledger history <b>without changing any balance</b>. For each <c>(user, currency, season)</c> scope it
/// folds a set of rows into one carry-forward <see cref="CurrencyLedgerSource.Checkpoint"/> entry (Amount = the sum
/// of the folded rows), then deletes them. Because balances are a live SUM over the ledger, the grand total is
/// unchanged and <c>RebuildWalletsAsync</c> still reconstructs the cache exactly. Two triggers:
/// <list type="bullet">
/// <item>time-based — <see cref="PruneAsync"/> folds rows older than a retention cutoff (daily sweep);</item>
/// <item>season-based — <see cref="CheckpointSeasonAsync"/> folds a whole season's rows when it's archived.</item>
/// </list>
/// The platform <see cref="CurrencyRetentionOptions.MaxLedgerRetentionDays"/> caps + defaults each guild's window.
/// </summary>
public sealed class LedgerPruneService(
    MusterDbContext db, IOptions<CurrencyRetentionOptions> options, ILogger<LedgerPruneService> logger)
{
    private int GlobalMaxDays => options.Value.MaxLedgerRetentionDays;

    /// <summary>Prune one guild against an already-effective <paramref name="retentionDays"/> (0 = never). Folds rows
    /// strictly older than the cutoff into per-scope checkpoints. Returns rows folded.</summary>
    public async Task<int> PruneAsync(ulong guildId, int retentionDays, CancellationToken ct = default)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var stale = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && e.OccurredAt < cutoff)
            .ToListAsync(ct);

        // Checkpoints are dated at the cutoff (never re-folded by the same run); an older checkpoint from a prior
        // run IS older than the new cutoff and gets folded in — so pruning is composable.
        return await FoldAsync(guildId, stale, cutoff, $"Ledger checkpoint (retention {retentionDays}d)", ct);
    }

    /// <summary>Fold every ledger row for an (archived) season into per-(user,currency) checkpoints, compacting the
    /// season's history while preserving its balances. Returns rows folded.</summary>
    public async Task<int> CheckpointSeasonAsync(ulong guildId, Guid seasonId, CancellationToken ct = default)
    {
        var rows = await db.CurrencyLedgerEntries
            .Where(e => e.GuildId == guildId && e.SeasonId == seasonId)
            .ToListAsync(ct);

        return await FoldAsync(guildId, rows, DateTimeOffset.UtcNow, "Season archive checkpoint", ct);
    }

    /// <summary>Prune every active guild by its <i>effective</i> retention (its <c>LedgerRetentionDays</c> combined
    /// with the platform cap/default). Returns total rows folded.</summary>
    public async Task<int> PruneAllAsync(CancellationToken ct = default)
    {
        var guilds = await db.Guilds.Where(g => g.IsActive).ToListAsync(ct);

        var total = 0;
        foreach (var g in guilds)
        {
            ct.ThrowIfCancellationRequested();
            var effective = LedgerRetention.Effective(g.Settings.LedgerRetentionDays, GlobalMaxDays);
            if (effective > 0)
            {
                total += await PruneAsync(g.Id, effective, ct);
            }
        }

        return total;
    }

    /// <summary>Fold the given rows into one <see cref="CurrencyLedgerSource.Checkpoint"/> per (user,currency,season),
    /// dated at <paramref name="checkpointAt"/>, then delete them — in one transaction. A scope that's already a lone
    /// checkpoint is skipped (nothing to compact), so re-runs are idempotent.</summary>
    private async Task<int> FoldAsync(
        ulong guildId, List<CurrencyLedgerEntry> rows, DateTimeOffset checkpointAt, string reason, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var folded = 0;
        foreach (var scope in rows.GroupBy(e => new { e.UserId, e.CurrencyId, e.SeasonId }))
        {
            var scopeRows = scope.ToList();
            if (scopeRows is [{ SourceType: CurrencyLedgerSource.Checkpoint }])
            {
                continue; // already a single carry-forward row — nothing to compact
            }

            db.CurrencyLedgerEntries.Add(new CurrencyLedgerEntry
            {
                GuildId = guildId,
                UserId = scope.Key.UserId,
                CurrencyId = scope.Key.CurrencyId,
                SeasonId = scope.Key.SeasonId,
                Amount = scopeRows.Sum(e => e.Amount),
                SourceType = CurrencyLedgerSource.Checkpoint,
                SourceId = null, // no idempotency key — the same-tx delete makes re-runs naturally idempotent
                OccurredAt = checkpointAt,
                Reason = $"{reason}: folded {scopeRows.Count} entries",
            });

            db.CurrencyLedgerEntries.RemoveRange(scopeRows);
            folded += scopeRows.Count;
        }

        if (folded > 0)
        {
            await db.SaveChangesAsync(ct); // insert checkpoints + delete detail in one transaction
            logger.LogInformation("Compacted {Rows} ledger row(s) for guild {GuildId} ({Reason}).", folded, guildId, reason);
        }

        return folded;
    }
}
