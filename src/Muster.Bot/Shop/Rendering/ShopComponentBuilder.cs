using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using NetCord;
using NetCord.Rest;

namespace Muster.Bot.Shop.Rendering;

/// <summary>A category choice for the browse filter (decoupled from the domain entity).</summary>
public record ShopCategoryOption(Guid Id, string Name);

/// <summary>A store choice for the store directory.</summary>
public record ShopStoreOption(Guid Id, string Name, int ListingCount);

/// <summary>
/// Builds the ephemeral shop browse components + the per-store channel hero card. Pure (no Discord/IO) so it's
/// unit-testable.
///
/// <para>Browse is <b>ephemeral</b>, re-rendered in place as the user filters/pages. State rides the custom_id as a
/// single <b>scope</b> token (one of "all", "c{categoryId}", "s{storeId}") plus a sort key + page — so a guild-wide
/// (category-filterable) view and a store-scoped view share one scheme and stay inside Discord's 100-char id limit.
/// The only persistent channel presence is the per-store hero card.</para>
/// </summary>
public static class ShopComponentBuilder
{
    // custom_id prefixes (also the [ComponentInteraction] route names).
    public const string Category = "shopcat";   // select — pick a category (value = scope token)
    public const string Sort = "shopsort";      // select — change sort (value = sort key)
    public const string Pick = "shoppick";      // select — open a listing (value = listing id)
    public const string Nav = "shopnav";        // buttons — prev / next / all-shops
    public const string Back = "shopback";      // button — return to the board from a detail view
    public const string MyOrders = "shopmine";  // button — show my orders
    public const string Home = "shophome";      // hero button — open this store's browse
    public const string Featured = "shopfeat";  // hero select — open a featured item
    public const string Directory = "shopdir";  // select — open a store from the directory
    public const string Buy = "sbuy";           // button — buy now (custom_id carries the chosen quantity)
    public const string QtySel = "sqty";        // select — choose buy quantity (1..min(5, stock))
    public const string Offer = "soffer";       // button — make an offer (opens a modal)
    public const string OfferModal = "sofferm"; // modal — the offer amount
    public const string OfferInput = "amount";  // modal text-input id

    public const string ScopeAll = "all";
    public const string SortNewest = "new";

    // Sort keys → (GetMarketAsync sort, desc). Featured always floats above the sort.
    private static readonly (string Key, string Label, string Sort, bool Desc)[] Sorts =
    [
        (SortNewest, "Newest", "created", true),
        ("plo", "Price ↑", "price", false),
        ("phi", "Price ↓", "price", true),
        ("name", "Name A–Z", "name", false),
    ];

    public static (string Sort, bool Desc) ResolveSort(string key)
    {
        foreach (var s in Sorts)
        {
            if (s.Key == key)
            {
                return (s.Sort, s.Desc);
            }
        }

        return ("created", true);
    }

    // ---- scope token (one of: "all" | "c{categoryId:N}" | "s{storeId:N}") ----

    public static string ScopeOf(Guid? categoryId = null, Guid? storeId = null) =>
        storeId is { } s ? "s" + s.ToString("N")
        : categoryId is { } c ? "c" + c.ToString("N")
        : ScopeAll;

    /// <summary>Parse a scope token into its category / store filter (both null = all shops).</summary>
    public static (Guid? CategoryId, Guid? StoreId) ParseScope(string? token)
    {
        if (string.IsNullOrEmpty(token) || token == ScopeAll || token.Length < 2
            || !Guid.TryParseExact(token[1..], "N", out var id))
        {
            return (null, null);
        }

        return token[0] switch
        {
            'c' => (id, null),
            's' => (null, id),
            _ => (null, null),
        };
    }

    public static bool IsStoreScope(string? token) => token is { Length: > 1 } && token[0] == 's';

    /// <summary>The board view. In guild scope: category filter + sort. In store scope: an "all shops" button + sort
    /// (no category filter — a single store's catalogue is small). Then the item picker + page nav + My orders.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Board(
        ShopBoardPage page, IReadOnlyList<ShopCategoryOption> categories, string scope, string sortKey, ulong guildId)
    {
        var rows = new List<IMessageComponentProperties>();
        var storeScope = IsStoreScope(scope);

        if (!storeScope)
        {
            // Category filter (≤ 25 incl. "All"); option values ARE scope tokens, carrying the sort in the id.
            var (catId, _) = ParseScope(scope);
            var catOptions = new List<StringMenuSelectOptionProperties>
            {
                new("All categories", ScopeAll) { Default = catId is null },
            };
            catOptions.AddRange(categories.Take(24).Select(c =>
                new StringMenuSelectOptionProperties(Trunc(c.Name, 100), ScopeOf(categoryId: c.Id)) { Default = catId == c.Id }));
            rows.Add(new StringMenuProperties($"{Category}:{guildId}:{sortKey}", catOptions) { Placeholder = "Filter by category…" });
        }

        // Sort (carries the scope).
        var sortOptions = Sorts.Select(s => new StringMenuSelectOptionProperties(s.Label, s.Key) { Default = s.Key == sortKey });
        rows.Add(new StringMenuProperties($"{Sort}:{guildId}:{scope}", sortOptions) { Placeholder = "Sort…" });

        // Item picker for this page.
        if (page.Items.Count > 0)
        {
            rows.Add(ItemSelect(page.Items, $"{Pick}:{guildId}:{scope}:{sortKey}"));
        }

        // Prev / Next / (All shops in store scope) / My orders.
        var nav = new List<ButtonProperties>();
        if (page.Page > 1)
        {
            nav.Add(new($"{Nav}:{guildId}:{scope}:{sortKey}:{page.Page - 1}", "◀ Prev", ButtonStyle.Secondary));
        }

        if (page.Page < page.TotalPages)
        {
            nav.Add(new($"{Nav}:{guildId}:{scope}:{sortKey}:{page.Page + 1}", "Next ▶", ButtonStyle.Secondary));
        }

        if (storeScope)
        {
            nav.Add(new($"{Nav}:{guildId}:{ScopeAll}:{sortKey}:1", "🏬 All shops", ButtonStyle.Secondary));
        }

        nav.Add(new($"{MyOrders}:{guildId}", "🧾 My orders", ButtonStyle.Primary));
        rows.Add(new ActionRowProperties(nav));

        return rows;
    }

    /// <summary>A reusable item-pick select (used by the board and by /shop search results).</summary>
    public static StringMenuProperties ItemSelect(IReadOnlyList<ShopListingCard> items, string customId)
    {
        var options = items.Take(25).Select(i => new StringMenuSelectOptionProperties(
            Trunc((i.Featured ? "⭐ " : "") + i.Name, 100), i.Id.ToString())
        {
            Description = Trunc($"{i.Price} {i.CurrencyCode} · {i.StoreName}", 100),
        });
        return new StringMenuProperties(customId, options) { Placeholder = "Open an item…" };
    }

    /// <summary>A listing detail view: an optional quantity picker (1..min(5, stock)) over Buy / Make offer (when
    /// active + open) and Back. The selected <paramref name="qty"/> rides the Buy button's id and shows in its label;
    /// picking a quantity re-renders this view with the new count.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Listing(ShopListingDetail d, string scope, string sortKey, ulong guildId, int qty = 1)
    {
        var active = d.Status == ShopListingStatus.Active;
        var stock = Math.Max(1, d.Quantity);
        var q = Math.Clamp(qty, 1, Math.Min(5, stock));
        var rows = new List<IMessageComponentProperties>();

        // Quantity picker only when more than one is in stock (capped at 5; bigger orders use /shop buy). The compact
        // listing id (:N) keeps the custom_id under Discord's 100-char limit alongside the scope + sort.
        if (active && stock > 1)
        {
            var qtyOptions = Enumerable.Range(1, Math.Min(5, stock))
                .Select(n => new StringMenuSelectOptionProperties($"Qty: {n}", n.ToString()) { Default = n == q });
            rows.Add(new StringMenuProperties($"{QtySel}:{guildId}:{d.Id:N}:{scope}:{sortKey}", qtyOptions) { Placeholder = "Quantity…" });
        }

        var buttons = new List<ButtonProperties>();
        if (active)
        {
            buttons.Add(new($"{Buy}:{guildId}:{d.Id}:{q}",
                q > 1 ? $"Buy ×{q} · {d.Price * q} {d.CurrencyCode}" : $"Buy · {d.Price} {d.CurrencyCode}", ButtonStyle.Success));
            if (d.OffersOpen)
            {
                buttons.Add(new($"{Offer}:{guildId}:{d.Id}", "Make offer", ButtonStyle.Secondary));
            }
        }

        buttons.Add(new($"{Back}:{guildId}:{scope}:{sortKey}:1", "◀ Back", ButtonStyle.Secondary));
        rows.Add(new ActionRowProperties(buttons));
        return rows;
    }

    /// <summary>The per-store hero card's components — featured items + Browse on <b>one row</b>. Featured items are
    /// buttons (a select can't share a row with a button in Discord); up to 4 fit alongside Browse. Tapping a featured
    /// button opens that item's detail ephemerally (the purchase confirm).</summary>
    public static IReadOnlyList<IMessageComponentProperties> StoreHero(ulong guildId, Guid storeId, IReadOnlyList<ShopListingCard> featured)
    {
        var buttons = new List<ButtonProperties>();
        foreach (var f in featured.Take(4))
        {
            buttons.Add(new($"{Featured}:{guildId}:{f.Id:N}", Trunc($"⭐ {f.Name}", 80), ButtonStyle.Secondary));
        }

        buttons.Add(new($"{Home}:{guildId}:{storeId:N}", "🛒 Browse the shop", ButtonStyle.Primary));
        return [new ActionRowProperties(buttons)];
    }

    /// <summary>The store-directory select (/shop stores) — pick a shop to browse it.</summary>
    public static StringMenuProperties StoreDirectory(ulong guildId, IReadOnlyList<ShopStoreOption> stores) =>
        new($"{Directory}:{guildId}", stores.Take(25).Select(s =>
            new StringMenuSelectOptionProperties(Trunc(s.Name, 100), s.Id.ToString("N"))
            {
                Description = $"{s.ListingCount} item(s)",
            }))
        { Placeholder = "Pick a shop to browse…" };

    public static ModalProperties OfferModalFor(ulong guildId, Guid listingId) =>
        new($"{OfferModal}:{guildId}:{listingId}", "Make an offer", new IModalComponentProperties[]
        {
            new LabelProperties("Your offer (whole number)",
                new TextInputProperties(OfferInput, TextInputStyle.Short) { Required = true, MaxLength = 18, Placeholder = "e.g. 100" }),
        });

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
