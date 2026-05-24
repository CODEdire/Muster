using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

/// <summary>
/// Idempotently onboards a guild: upserts the <see cref="Guild"/> row and seeds the defaults a
/// guild needs to start scoring — the built-in seasonal POINTS currency and an active season.
/// Safe to call on every <c>GuildCreate</c> (including gateway reconnects).
/// </summary>
public class GuildProvisioningService(MusterDbContext db)
{
    public const string PointsCurrencyCode = "POINTS";

    public async Task EnsureGuildAsync(ulong guildId, string name, string? iconHash, CancellationToken ct = default)
    {
        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        if (guild is null)
        {
            db.Guilds.Add(new Guild
            {
                Id = guildId,
                Name = name,
                IconHash = iconHash,
                JoinedAt = DateTimeOffset.UtcNow,
                IsActive = true,
            });
        }
        else
        {
            guild.Name = name;
            guild.IconHash = iconHash;
            guild.IsActive = true;
        }

        var hasPoints = await db.Currencies.AnyAsync(c => c.GuildId == guildId && c.Code == PointsCurrencyCode, ct);
        if (!hasPoints)
        {
            db.Currencies.Add(new Currency
            {
                Id = Guid.NewGuid(),
                GuildId = guildId,
                Code = PointsCurrencyCode,
                Name = "Points",
                IsSeasonal = true,
                IsSpendable = false,
            });
        }

        var hasActiveSeason = await db.Seasons.AnyAsync(s => s.GuildId == guildId && s.Status == SeasonStatus.Active, ct);
        if (!hasActiveSeason)
        {
            db.Seasons.Add(new Season
            {
                Id = Guid.NewGuid(),
                GuildId = guildId,
                Name = "Season 1",
                StartsAt = DateTimeOffset.UtcNow,
                Status = SeasonStatus.Active,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
