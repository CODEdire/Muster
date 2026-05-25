using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Ledger;

public enum CurrencyOperationStatus
{
    Ok,
    CurrencyNotFound,
    InsufficientFunds,
}

public record CurrencyOperationResult(CurrencyOperationStatus Status, long Balance);

/// <summary>
/// Mint/spend operations on spendable currencies, used by the public API. Spending is overdraft-checked
/// when Muster is the balance authority (Internal/Hybrid); External-mode currencies skip the check since
/// the external system owns the balance and Muster's ledger is a shadow/audit trail.
/// </summary>
public class CurrencyService(MusterDbContext db, AwardService awards)
{
    public async Task<long?> GetBalanceAsync(ulong guildId, string code, ulong userId, CancellationToken ct = default)
    {
        var currency = await FindAsync(guildId, code, ct);
        if (currency is null)
        {
            return null;
        }

        Guid? seasonId = currency.IsSeasonal ? await ActiveSeasonIdAsync(guildId, ct) : null;
        return await BalanceAsync(guildId, userId, currency.Id, seasonId, ct);
    }

    public async Task<CurrencyOperationResult> MintAsync(
        ulong guildId, string code, ulong userId, long amount, string reason, CancellationToken ct = default)
    {
        var currency = await FindAsync(guildId, code, ct);
        if (currency is null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.CurrencyNotFound, 0);
        }

        await awards.AwardAsync(guildId, userId, currency.Id, amount, LedgerSourceType.Connector, sourceId: null, reason, ct);
        return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, userId, ct));
    }

    public async Task<CurrencyOperationResult> SpendAsync(
        ulong guildId, string code, ulong userId, long amount, string reason, CancellationToken ct = default)
    {
        var currency = await FindAsync(guildId, code, ct);
        if (currency is null)
        {
            return new CurrencyOperationResult(CurrencyOperationStatus.CurrencyNotFound, 0);
        }

        // Only enforce the balance when Muster is the authority for it.
        if (currency.Mode != CurrencyMode.External)
        {
            Guid? seasonId = currency.IsSeasonal ? await ActiveSeasonIdAsync(guildId, ct) : null;
            var balance = await BalanceAsync(guildId, userId, currency.Id, seasonId, ct);
            if (balance < amount)
            {
                return new CurrencyOperationResult(CurrencyOperationStatus.InsufficientFunds, balance);
            }
        }

        await awards.AwardAsync(guildId, userId, currency.Id, -amount, LedgerSourceType.Connector, sourceId: null, reason, ct);
        return new CurrencyOperationResult(CurrencyOperationStatus.Ok, await CurrentBalanceAsync(guildId, code, userId, ct));
    }

    private Task<Currency?> FindAsync(ulong guildId, string code, CancellationToken ct)
        => db.Currencies.FirstOrDefaultAsync(c => c.GuildId == guildId && c.Code == code, ct);

    private async Task<long> CurrentBalanceAsync(ulong guildId, string code, ulong userId, CancellationToken ct)
        => await GetBalanceAsync(guildId, code, userId, ct) ?? 0;

    private async Task<Guid?> ActiveSeasonIdAsync(ulong guildId, CancellationToken ct)
        => await db.Seasons.Where(s => s.GuildId == guildId && s.Status == SeasonStatus.Active)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);

    private async Task<long> BalanceAsync(ulong guildId, ulong userId, Guid currencyId, Guid? seasonId, CancellationToken ct)
        => await db.LedgerEntries
            .Where(e => e.GuildId == guildId && e.UserId == userId && e.CurrencyId == currencyId && e.SeasonId == seasonId)
            .SumAsync(e => (long?)e.Amount, ct) ?? 0;
}
