using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Quests;
using Muster.Persistence;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>
/// The default quest types a guild starts with — activity archetypes with Material Symbols icons (the quest card's
/// visual, since quests carry no uploaded art). Mirrors <c>ShopSeed</c>: stages rows onto the
/// <see cref="MusterDbContext"/> (the caller saves), idempotent by name, and version-gated so existing guilds pick up
/// new defaults on a later seed pass without resurrecting ones an admin deleted. Part of the wider
/// <c>GuildSeed</c> catalog.
/// </summary>
public static class QuestTypeSeed
{
    private record SeedItem(string Name, string Icon, int Sort, int IntroducedIn);

    // Icons are Material Symbols names used verbatim on the card. Introduced in seed version 3 (when quest types shipped).
    private static readonly SeedItem[] Types =
    [
        new("Gathering", "grass", 10, 3),
        new("Combat", "swords", 20, 3),
        new("Bounty", "crisis_alert", 30, 3),
        new("Delivery", "local_shipping", 40, 3),
        new("Escort", "shield", 50, 3),
        new("Exploration", "explore", 60, 3),
        new("Mining", "diamond", 70, 3),
        new("Crafting", "build", 80, 3),
        new("Trade", "storefront", 90, 3),
        new("Raid", "groups", 100, 3),
        new("Salvage", "recycling", 110, 3),
        new("Recovery", "inventory_2", 120, 3),
    ];

    /// <summary>Stage the guild's missing default quest types. Adds an entry when <paramref name="force"/> or its
    /// version is newer than <paramref name="seedFrom"/> and it isn't already present by name. Returns rows added.</summary>
    public static async Task<int> StageAsync(MusterDbContext db, ulong guildId, int seedFrom, bool force, CancellationToken ct = default)
    {
        var added = 0;

        var existing = (await db.QuestTypes.Where(t => t.GuildId == guildId).Select(t => t.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Types)
        {
            if ((force || s.IntroducedIn > seedFrom) && existing.Add(s.Name))
            {
                db.QuestTypes.Add(QuestType.Create(guildId, s.Name, s.Sort, s.Icon));
                added++;
            }
        }

        return added;
    }
}
