using Microsoft.EntityFrameworkCore;
using Muster.Domain;
using Muster.Domain.Entities.Currencies;
using Muster.Domain.Enums;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>
/// Default spendable currencies a guild starts with. The seasonal POINTS currency is seeded inline by
/// <see cref="Membership.GuildProvisioningService"/>; this stages the spendable <c>COIN</c> wallet so a fresh
/// guild's shop is usable without manual currency setup. Idempotent: skips by code and (unless <c>force</c>) only
/// adds entries newer than the guild's recorded seed version. Part of the <see cref="GuildSeed"/> catalog.
/// </summary>
public static class CurrencySeed
{
    private record SeedCurrency(string Code, string Name, bool Spendable, bool Seasonal, int IntroducedIn);

    private static readonly SeedCurrency[] Currencies =
    [
        new(CurrencyCodes.CoinCode, "Coin", Spendable: true, Seasonal: false, IntroducedIn: 2),
    ];

    /// <summary>Stage the guild's missing default currencies. Returns rows added.</summary>
    public static async Task<int> StageAsync(MusterDbContext db, ulong guildId, int seedFrom, bool force, CancellationToken ct = default)
    {
        var added = 0;

        var existing = (await db.Currencies.Where(c => c.GuildId == guildId).Select(c => c.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The guild's first spendable currency becomes the default/primary (wallet opens on it). Skip if one is set.
        var hasPrimary = await db.Currencies.AnyAsync(c => c.GuildId == guildId && c.IsPrimary, ct);

        foreach (var s in Currencies)
        {
            if ((force || s.IntroducedIn > seedFrom) && existing.Add(s.Code))
            {
                var makePrimary = s.Spendable && !hasPrimary;
                db.Currencies.Add(new Currency
                {
                    Id = Guid.NewGuid(),
                    GuildId = guildId,
                    Code = s.Code,
                    Name = s.Name,
                    IsSpendable = s.Spendable,
                    IsSeasonal = s.Seasonal,
                    IsTransferable = s.Spendable,
                    IsPrimary = makePrimary,
                    Mode = CurrencyMode.Internal,
                });
                if (makePrimary)
                {
                    hasPrimary = true;
                }
                added++;
            }
        }

        return added;
    }
}
