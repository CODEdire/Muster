using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Shops;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>
/// The default shop categories + store types (with curated icons) a guild starts with. Stages rows onto the
/// <see cref="MusterDbContext"/> (the caller saves) and is idempotent: skips entries that already exist by name and
/// (unless <c>force</c>) only adds entries newer than the guild's recorded seed version. Part of the wider
/// <see cref="GuildSeed"/> catalog — that type owns the shared version + saving.
/// </summary>
public static class ShopSeed
{
    private record SeedItem(string Name, string Icon, int Sort, int IntroducedIn);

    // Item categories (game-shop themed; icon keys are Muster.Contracts.ShopIcons keys).
    private static readonly SeedItem[] Categories =
    [
        new("Weapons", "weapons", 10, 1),
        new("Armor", "armor", 20, 1),
        new("Ammunition", "ammo", 30, 1),
        new("Consumables", "consumables", 40, 1),
        new("Magic", "magic", 50, 1),
        new("Components", "components", 60, 1),
        new("Resources", "resources", 70, 1),
        new("Tools", "tools", 80, 1),
        new("Apparel", "apparel", 90, 1),
        new("Mounts", "mounts", 100, 1),
        new("Misc", "general", 110, 1),
    ];

    // Store types (whole-shop archetypes).
    private static readonly SeedItem[] Types =
    [
        new("General Store", "general", 10, 1),
        new("Weaponsmith", "weapons", 20, 1),
        new("Armorer", "armor", 30, 1),
        new("Apothecary", "consumables", 40, 1),
        new("Arcanist", "magic", 50, 1),
        new("Quartermaster", "ammo", 60, 1),
        new("Shipwright", "ships", 70, 1),
        new("Mechanic", "components", 80, 1),
        new("Prospector", "mining", 90, 1),
        new("Outfitter", "apparel", 100, 1),
        new("Trader", "resources", 110, 1),
    ];

    /// <summary>Stage the guild's missing default categories/types. Adds an entry when <paramref name="force"/> or its
    /// version is newer than <paramref name="seedFrom"/> and it isn't already present by name. Returns rows added.</summary>
    public static async Task<int> StageAsync(MusterDbContext db, ulong guildId, int seedFrom, bool force, CancellationToken ct = default)
    {
        var added = 0;

        var existingCats = (await db.ShopCategories.Where(c => c.GuildId == guildId).Select(c => c.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Categories)
        {
            if ((force || s.IntroducedIn > seedFrom) && existingCats.Add(s.Name))
            {
                db.ShopCategories.Add(ShopCategory.Create(guildId, s.Name, s.Sort, null, s.Icon));
                added++;
            }
        }

        var existingTypes = (await db.ShopStoreTypes.Where(t => t.GuildId == guildId).Select(t => t.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Types)
        {
            if ((force || s.IntroducedIn > seedFrom) && existingTypes.Add(s.Name))
            {
                db.ShopStoreTypes.Add(ShopStoreType.Create(guildId, s.Name, s.Sort, s.Icon));
                added++;
            }
        }

        return added;
    }
}
