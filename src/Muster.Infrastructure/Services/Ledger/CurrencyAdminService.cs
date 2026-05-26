using System.Text.RegularExpressions;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;

namespace Muster.Infrastructure.Services.Ledger;

public record CurrencyView(Guid Id, string Code, string Name, bool IsSeasonal, bool IsSpendable, CurrencyMode Mode, bool IsSystem);

/// <summary>
/// Admin management of a guild's currencies. The seeded POINTS currency is treated as a protected
/// system currency (its code and seasonal nature can't change). Other currencies (e.g. a spendable
/// COIN for bounties/loot) are created and tuned here.
/// </summary>
public partial class CurrencyAdminService(MusterDbContext db)
{
    public const string PointsCode = "POINTS";

    [GeneratedRegex(@"^[A-Z0-9_]{2,16}$")]
    private static partial Regex CodePattern();

    public async Task<IReadOnlyList<CurrencyView>> ListAsync(ulong guildId, CancellationToken ct = default)
        => await db.Currencies
            .Where(c => c.GuildId == guildId)
            .OrderBy(c => c.Code)
            .Select(c => new CurrencyView(c.Id, c.Code, c.Name, c.IsSeasonal, c.IsSpendable, c.Mode, c.Code == PointsCode))
            .ToListAsync(ct);

    public async Task<CommandResult> CreateAsync(
        ulong guildId, string code, string name, bool isSeasonal, bool isSpendable, CurrencyMode mode,
        CancellationToken ct = default)
    {
        code = (code ?? string.Empty).Trim().ToUpperInvariant();

        if (!CodePattern().IsMatch(code))
        {
            return CommandResult.Error("Code must be 2–16 characters: A–Z, 0–9, or underscore.");
        }

        if (code == PointsCode)
        {
            return CommandResult.Error("POINTS is reserved.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide a name.");
        }

        if (await db.CurrencyExistsAsync(guildId, code, ct))
        {
            return CommandResult.Error($"A currency with code {code} already exists.");
        }

        db.Currencies.Add(new Currency
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Code = code,
            Name = name.Trim(),
            IsSeasonal = isSeasonal,
            IsSpendable = isSpendable,
            Mode = mode,
        });
        await db.SaveChangesAsync(ct);

        return CommandResult.Ok($"Created currency **{code}** ({name.Trim()}).");
    }

    /// <summary>Update mutable fields (name, spendable, mode). Code and seasonal nature are fixed at creation.</summary>
    public async Task<CommandResult> UpdateAsync(
        ulong guildId, Guid currencyId, string name, bool isSpendable, CurrencyMode mode, CancellationToken ct = default)
    {
        var currency = await db.FindCurrencyByIdAsync(guildId, currencyId, ct);
        if (currency is null)
        {
            return CommandResult.Error("Currency not found.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide a name.");
        }

        currency.Name = name.Trim();
        currency.IsSpendable = isSpendable;
        currency.Mode = mode;
        await db.SaveChangesAsync(ct);

        return CommandResult.Ok($"Updated currency **{currency.Code}**.");
    }
}
