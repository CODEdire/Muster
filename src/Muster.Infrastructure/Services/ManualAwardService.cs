using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

/// <summary>Admin manual / bulk awards for off-platform contributions.</summary>
public class ManualAwardService(MusterDbContext db, AwardService awards)
{
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
