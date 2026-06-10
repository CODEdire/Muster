namespace Muster.Contracts;

/// <summary>One curated shop/category icon: a stable <see cref="Key"/> stored on the entity, plus how to render it
/// on the web (<see cref="Material"/> = a Material Symbols name) and in Discord (<see cref="Emoji"/>).</summary>
public record ShopIconDef(string Key, string Label, string Material, string Emoji);

/// <summary>
/// The curated set of shop-type / category icons admins may choose from — common game-shop archetypes across RPGs,
/// FPS, and sims (e.g. Star Citizen). Stored by <see cref="ShopIconDef.Key"/>; the web renders the Material Symbol,
/// Discord uses the emoji. Kept here in Contracts so every surface maps the same keys.
/// </summary>
public static class ShopIcons
{
    public static readonly IReadOnlyList<ShopIconDef> All =
    [
        new("general", "General store", "storefront", "🛒"),
        new("weapons", "Weapons", "swords", "⚔️"),
        new("armor", "Armor", "shield", "🛡️"),
        new("ammo", "Ammunition", "adjust", "🎯"),
        new("consumables", "Consumables / potions", "science", "🧪"),
        new("magic", "Magic / arcane", "auto_awesome", "✨"),
        new("ships", "Ships", "rocket_launch", "🚀"),
        new("vehicles", "Vehicles", "directions_car", "🚗"),
        new("components", "Components / parts", "settings", "⚙️"),
        new("mining", "Mining / gems", "diamond", "💎"),
        new("resources", "Resources / commodities", "inventory_2", "📦"),
        new("food", "Food / provisions", "restaurant", "🍖"),
        new("apparel", "Apparel / cosmetics", "checkroom", "🧥"),
        new("tools", "Tools", "build", "🛠️"),
        new("medical", "Medical", "medical_services", "⚕️"),
        new("fuel", "Fuel / refuel", "local_gas_station", "⛽"),
        new("tech", "Tech / electronics", "memory", "💻"),
        new("mounts", "Mounts / pets", "pets", "🐾"),
    ];

    public static ShopIconDef? Find(string? key) =>
        string.IsNullOrEmpty(key) ? null : All.FirstOrDefault(i => i.Key == key);

    /// <summary>The Material Symbols name for a key (null if unknown/none) — for web rendering.</summary>
    public static string? Material(string? key) => Find(key)?.Material;

    /// <summary>The emoji for a key (null if unknown/none) — for Discord.</summary>
    public static string? Emoji(string? key) => Find(key)?.Emoji;
}
