using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Ledger;

/// <summary>Admin manual / bulk awards for off-platform contributions.</summary>
public class ManualAwardService(MusterDbContext db, AwardService awards)
{
    /// <summary>Manually award the guild's POINTS currency to a member.</summary>
    public async Task AwardPointsAsync(
        ulong guildId, ulong userId, long amount, string reason, ulong awardedBy, CancellationToken ct = default)
    {
        var points = await db.Currencies.FirstOrDefaultAsync(
            c => c.GuildId == guildId && c.Code == GuildProvisioningService.PointsCurrencyCode, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

        await AwardAsync(guildId, userId, points.Id, amount, reason, awardedBy, ct);
    }

    /// <summary>Manually award a currency (by code). Returns false if the currency is unknown.</summary>
    public async Task<bool> AwardByCodeAsync(
        ulong guildId, ulong userId, string currencyCode, long amount, string reason, ulong awardedBy, CancellationToken ct = default)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.GuildId == guildId && c.Code == code, ct);
        if (currency is null)
        {
            return false;
        }

        await AwardAsync(guildId, userId, currency.Id, amount, reason, awardedBy, ct);
        return true;
    }

    public async Task AwardAsync(
        ulong guildId, ulong userId, Guid currencyId, long amount, string reason,
        ulong awardedBy, CancellationToken ct = default)
    {
        var record = new ManualAward
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            UserId = userId,
            CurrencyId = currencyId,
            Amount = amount,
            Reason = reason,
            AwardedBy = awardedBy,
            AwardedAt = DateTimeOffset.UtcNow,
        };
        db.ManualAwards.Add(record);
        await db.SaveChangesAsync(ct);

        await awards.AwardAsync(
            guildId, userId, currencyId, amount,
            LedgerSourceType.ManualAward, $"award:{record.Id}", reason, ct);
    }

    /// <summary>Award the same amount to many members at once (e.g. everyone in a voice channel).</summary>
    public async Task AwardBulkAsync(
        ulong guildId, IEnumerable<ulong> userIds, Guid currencyId, long amount, string reason,
        ulong awardedBy, CancellationToken ct = default)
    {
        foreach (var userId in userIds.Distinct())
        {
            await AwardAsync(guildId, userId, currencyId, amount, reason, awardedBy, ct);
        }
    }
}
