using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Membership;

/// <summary>
/// Idempotently onboards a guild: upserts the <see cref="Guild"/> row and seeds the defaults a
/// guild needs to start scoring — the built-in seasonal POINTS currency and an active season.
/// Safe to call on every <c>GuildCreate</c> (including gateway reconnects).
/// </summary>
public class GuildProvisioningService(MusterDbContext db)
{
    public const string PointsCurrencyCode = Currencies.PointsCode;

    public async Task EnsureGuildAsync(
        ulong guildId, string name, string? iconHash, ulong ownerId = 0, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            db.Guilds.Add(new Guild
            {
                Id = guildId,
                Name = name,
                IconHash = iconHash,
                OwnerId = ownerId,
                JoinedAt = DateTimeOffset.UtcNow,
                IsActive = true,
            });
        }
        else
        {
            guild.Name = name;
            guild.IconHash = iconHash;
            guild.IsActive = true;
            if (ownerId != 0)
            {
                guild.OwnerId = ownerId;
            }
        }

        var hasPoints = await db.CurrencyExistsAsync(guildId, PointsCurrencyCode, ct);
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

        var hasActiveSeason = await db.HasActiveSeasonAsync(guildId, ct);
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

    /// <summary>Mark a guild inactive when the bot is removed from it.</summary>
    public async Task MarkInactiveAsync(ulong guildId, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is not null)
        {
            guild.IsActive = false;
            await db.SaveChangesAsync(ct);
        }
    }
}
