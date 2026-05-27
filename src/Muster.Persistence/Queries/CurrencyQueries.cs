using Microsoft.EntityFrameworkCore;
using Muster.Domain;
using Muster.Domain.Entities;

namespace Muster.Persistence.Queries;

/// <summary>A currency projected for the public API / lists (mode flattened to a string).</summary>
public record CurrencySummary(Guid Id, string Code, string Name, string Mode, bool IsSeasonal, bool IsSpendable);

/// <summary>Queries over guild currencies.</summary>
public static class CurrencyQueries
{
    /// <summary>A guild's currencies (code-ordered) projected for the public API.</summary>
    public static async Task<List<CurrencySummary>> ListCurrencySummariesAsync(
        this MusterDbContext db, ulong guildId, CancellationToken ct = default)
    {
        var rows = await db.Currencies
            .Where(c => c.GuildId == guildId)
            .OrderBy(c => c.Code)
            .Select(c => new { c.Id, c.Code, c.Name, c.Mode, c.IsSeasonal, c.IsSpendable })
            .ToListAsync(ct);

        return rows.Select(c => new CurrencySummary(c.Id, c.Code, c.Name, c.Mode.ToString(), c.IsSeasonal, c.IsSpendable)).ToList();
    }

    /// <summary>Find a currency by its (case-insensitive) code within a guild.</summary>
    public static Task<Currency?> FindCurrencyAsync(this MusterDbContext db, ulong guildId, string? code, CancellationToken ct = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        return db.Currencies.FirstOrDefaultAsync(c => c.GuildId == guildId && c.Code == normalized, ct);
    }

    /// <summary>Find a currency by id within a guild.</summary>
    public static Task<Currency?> FindCurrencyByIdAsync(this MusterDbContext db, ulong guildId, Guid currencyId, CancellationToken ct = default)
        => db.Currencies.FirstOrDefaultAsync(c => c.Id == currencyId && c.GuildId == guildId, ct);

    /// <summary>The guild's POINTS currency, or null if it isn't provisioned.</summary>
    public static Task<Currency?> FindPointsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Currencies.FirstOrDefaultAsync(c => c.GuildId == guildId && c.Code == CurrencyCodes.PointsCode, ct);

    /// <summary>All currencies in a guild.</summary>
    public static Task<List<Currency>> ListCurrenciesAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Currencies.Where(c => c.GuildId == guildId).ToListAsync(ct);

    /// <summary>A guild's currencies (code-ordered, <b>untracked</b>) for read-only display/admin mapping — no tracking
    /// so it can't interfere with a later write in the same unit of work.</summary>
    public static Task<List<Currency>> ListCurrenciesReadOnlyAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Currencies.AsNoTracking().Where(c => c.GuildId == guildId).OrderBy(c => c.Code).ToListAsync(ct);

    /// <summary>Find a currency by id within a guild, <b>untracked</b> (read-only display).</summary>
    public static Task<Currency?> FindCurrencyByIdReadOnlyAsync(this MusterDbContext db, ulong guildId, Guid currencyId, CancellationToken ct = default)
        => db.Currencies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == currencyId && c.GuildId == guildId, ct);

    /// <summary>Map of currency id → code for a guild (for rendering reward lines).</summary>
    public static Task<Dictionary<Guid, string>> CurrencyCodeMapAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Currencies.Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.Id, c => c.Code, ct);

    /// <summary>Whether a currency code is already taken in a guild.</summary>
    public static Task<bool> CurrencyExistsAsync(this MusterDbContext db, ulong guildId, string code, CancellationToken ct = default)
        => db.Currencies.AnyAsync(c => c.GuildId == guildId && c.Code == code, ct);

    // --- Bulk adjustment batches ---

    /// <summary>Find a queued bulk batch by id (tracked, for the worker to update progress).</summary>
    public static Task<CurrencyBulkBatch?> FindBulkBatchAsync(this MusterDbContext db, Guid batchId, CancellationToken ct = default)
        => db.CurrencyBulkBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct);

    /// <summary>Find a bulk batch scoped to its guild (for progress polling).</summary>
    public static Task<CurrencyBulkBatch?> FindBulkBatchAsync(this MusterDbContext db, ulong guildId, Guid batchId, CancellationToken ct = default)
        => db.CurrencyBulkBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId && b.GuildId == guildId, ct);

    // --- Outbound webhooks ---

    /// <summary>A guild's webhook subscriptions (newest first, untracked) for the admin list.</summary>
    public static Task<List<CurrencyWebhook>> ListWebhooksAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.CurrencyWebhooks.AsNoTracking().Where(w => w.GuildId == guildId).OrderByDescending(w => w.CreatedAt).ToListAsync(ct);

    /// <summary>Find a webhook within a guild (tracked, for edit/enable/delete + health updates).</summary>
    public static Task<CurrencyWebhook?> FindWebhookAsync(this MusterDbContext db, ulong guildId, Guid id, CancellationToken ct = default)
        => db.CurrencyWebhooks.FirstOrDefaultAsync(w => w.Id == id && w.GuildId == guildId, ct);

    /// <summary>A guild's enabled webhooks (tracked, so the fan-out can fold delivery health back in).</summary>
    public static Task<List<CurrencyWebhook>> EnabledWebhooksAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.CurrencyWebhooks.Where(w => w.GuildId == guildId && w.Enabled).ToListAsync(ct);
}
