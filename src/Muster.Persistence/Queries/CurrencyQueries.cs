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

    /// <summary>Map of currency id → code for a guild (for rendering reward lines).</summary>
    public static Task<Dictionary<Guid, string>> CurrencyCodeMapAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Currencies.Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.Id, c => c.Code, ct);

    /// <summary>Whether a currency code is already taken in a guild.</summary>
    public static Task<bool> CurrencyExistsAsync(this MusterDbContext db, ulong guildId, string code, CancellationToken ct = default)
        => db.Currencies.AnyAsync(c => c.GuildId == guildId && c.Code == code, ct);
}
