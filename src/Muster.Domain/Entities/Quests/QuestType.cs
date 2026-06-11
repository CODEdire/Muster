using Muster.Domain.Entities.Guilds;

namespace Muster.Domain.Entities.Quests;

/// <summary>
/// A guild-curated quest <b>type</b> (controlled vocabulary, admin-managed) — classifies a quest by activity
/// (e.g. "Gathering", "Combat", "Escort"). Mirrors <see cref="Muster.Domain.Entities.Shops.ShopStoreType"/>. Unlike
/// the shop's curated icon set, a quest type's <see cref="Icon"/> is a <b>Material Symbols</b> name used directly as
/// the quest card's visual (quests carry no uploaded image — the type icon stands in for one).
/// </summary>
public class QuestType
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Display order on pickers/filters (ascending).</summary>
    public int Sort { get; set; }

    /// <summary>Material Symbols icon name (e.g. <c>swords</c>, <c>grass</c>) — the quest card's visual. Null = a
    /// neutral default icon is shown.</summary>
    public string? Icon { get; set; }

    public Guild? Guild { get; set; }

    public static QuestType Create(ulong guildId, string name, int sort, string? icon = null)
        => new() { Id = Guid.NewGuid(), GuildId = guildId, Name = name.Trim(), Sort = sort, Icon = icon };
}
