using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Ledger;

/// <summary>
/// The single path that writes to the ledger. Every participation method awards through here so that
/// idempotency, season scoping, and wallet balances stay consistent. Awards are idempotent on
/// <c>(SourceType, SourceId)</c>: re-processing the same source returns the existing entry rather than
/// double-crediting (important under gateway redelivery).
/// </summary>
public class AwardService(MusterDbContext db)
{
    /// <summary>Award an amount of a currency identified by its <paramref name="currencyId"/>.</summary>
    public async Task<LedgerEntry> AwardAsync(
        ulong guildId,
        ulong userId,
        Guid currencyId,
        long amount,
        LedgerSourceType sourceType,
        string? sourceId,
        string reason,
        CancellationToken ct = default)
    {
        var entry = await StageAsync(guildId, userId, currencyId, amount, sourceType, sourceId, reason, ct);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// Stage a ledger entry + wallet update WITHOUT saving, so callers can commit several legs together
    /// with a state change in one transaction (used by the escrow engine). Idempotent on (SourceType,
    /// SourceId): a duplicate returns the existing entry and stages nothing.
    /// </summary>
    internal async Task<LedgerEntry> StageAsync(
        ulong guildId,
        ulong userId,
        Guid currencyId,
        long amount,
        LedgerSourceType sourceType,
        string? sourceId,
        string reason,
        CancellationToken ct = default)
    {
        if (sourceId is not null)
        {
            var existing = await db.LedgerEntries
                .FirstOrDefaultAsync(e => e.SourceType == sourceType && e.SourceId == sourceId, ct);
            if (existing is not null)
            {
                return existing;
            }
        }

        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.Id == currencyId && c.GuildId == guildId, ct)
            ?? throw new InvalidOperationException($"Currency {currencyId} not found for guild {guildId}.");

        Guid? seasonId = null;
        if (currency.IsSeasonal)
        {
            seasonId = await db.Seasons
                .Where(s => s.GuildId == guildId && s.Status == SeasonStatus.Active)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
        }

        var entry = new LedgerEntry
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
        db.LedgerEntries.Add(entry);

        var wallet = await db.Wallets.FirstOrDefaultAsync(
            w => w.GuildId == guildId && w.UserId == userId && w.CurrencyId == currency.Id && w.SeasonId == seasonId, ct);
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

        return entry;
    }

    /// <summary>Convenience: award the guild's seasonal POINTS currency.</summary>
    public async Task<LedgerEntry> AwardPointsAsync(
        ulong guildId,
        ulong userId,
        long amount,
        LedgerSourceType sourceType,
        string? sourceId,
        string reason,
        CancellationToken ct = default)
    {
        var points = await db.Currencies.FirstOrDefaultAsync(
            c => c.GuildId == guildId && c.Code == GuildProvisioningService.PointsCurrencyCode, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

        return await AwardAsync(guildId, userId, points.Id, amount, sourceType, sourceId, reason, ct);
    }

    /// <summary>Stage a POINTS award (no save) so it commits in the caller's unit of work.</summary>
    internal async Task StagePointsAsync(
        ulong guildId, ulong userId, long amount, LedgerSourceType sourceType, string? sourceId, string reason,
        CancellationToken ct = default)
    {
        var points = await db.Currencies.FirstOrDefaultAsync(
            c => c.GuildId == guildId && c.Code == GuildProvisioningService.PointsCurrencyCode, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

        await StageAsync(guildId, userId, points.Id, amount, sourceType, sourceId, reason, ct);
    }
}
