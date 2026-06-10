using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using NetCord;
using NetCord.Rest;

namespace Muster.Bot.Shop.Rendering;

/// <summary>
/// Renders the ephemeral shop browse hub into Discord embeds — a board summary and a single-listing detail card.
/// Pure (no Discord/IO) so it's unit-testable. Unlike quests/musters there is no persistent channel card; this is
/// the per-user browse surface, so the embed is a compact catalogue snapshot the components page through.
/// </summary>
public static class ShopEmbedRenderer
{
    private static readonly Color Accent = new(0x6366F1); // indigo — the shop accent

    /// <summary>The board: a compact list of this page's items + a footer with totals / active scope (store/category).</summary>
    public static EmbedProperties Board(ShopBoardPage page, string? scopeLabel)
    {
        var description = page.Items.Count == 0
            ? "Nothing for sale here yet."
            : string.Join("\n", page.Items.Take(10).Select(i => $"{(i.Featured ? "⭐ " : "• ")}**{i.Name}** — 🪙 {i.Price} {i.CurrencyCode} · {i.StoreName}"));

        var scope = scopeLabel is null ? "" : $" · {scopeLabel}";
        return new EmbedProperties
        {
            Title = scopeLabel is null ? "🛒 Shop" : $"🛒 {scopeLabel}",
            Description = Truncate(description, 4000),
            Color = Accent,
            Footer = new EmbedFooterProperties
            {
                Text = $"{page.Total} item(s){scope} · page {page.Page}/{Math.Max(1, page.TotalPages)} · pick one below to view",
            },
        };
    }

    /// <summary>A single listing's detail card — price, store, seller, stock, category, tags, description.</summary>
    public static EmbedProperties Listing(ShopListingDetail d, ulong guildId, string? webBaseUrl = null)
    {
        var fields = new List<EmbedFieldProperties>
        {
            new() { Name = "Price", Value = $"🪙 {d.Price} {d.CurrencyCode}", Inline = true },
            new() { Name = "Store", Value = d.StoreName, Inline = true },
            new() { Name = "Seller", Value = d.SellerName, Inline = true },
        };

        if (d.Quantity > 1)
        {
            fields.Add(new() { Name = "Available", Value = d.Quantity.ToString(), Inline = true });
        }

        if (d.CategoryName is { } cn)
        {
            fields.Add(new() { Name = "Category", Value = cn, Inline = true });
        }

        if (d.Tags.Count > 0)
        {
            fields.Add(new() { Name = "Tags", Value = string.Join(" ", d.Tags.Select(t => $"`#{t}`")), Inline = false });
        }

        return new EmbedProperties
        {
            Title = d.Name,
            Url = WebUrl(webBaseUrl, guildId, d.Id),
            Description = string.IsNullOrWhiteSpace(d.Description) ? null : Truncate(d.Description, 1000),
            Color = Accent,
            Fields = fields,
            Footer = new EmbedFooterProperties { Text = $"Status: {d.Status}" },
            Timestamp = d.CreatedAt,
        };
    }

    /// <summary>A store's persistent hero card. URLs are pre-resolved by the caller. The <b>author line is the shop
    /// name</b> (linked, with the logo as its avatar — or the type's emoji when there's no logo); the banner is the
    /// big embed image; the type is a field; the owner + last-updated go in the footer.</summary>
    public static EmbedProperties StoreHero(
        string name, string? typeName, string? typeEmoji, string? description, string ownerName, string? ownerAvatarUrl,
        int activeCount, double ratingAvg, int ratingCount, string? logoUrl, string? bannerUrl, string? accentColor,
        IReadOnlyList<ShopListingCard> featured, string? storefrontUrl, DateTimeOffset updatedAt)
    {
        var fields = new List<EmbedFieldProperties>();
        if (typeName is { Length: > 0 })
        {
            fields.Add(new() { Name = "Type", Value = (typeEmoji is { } te ? te + " " : "") + typeName, Inline = true });
        }

        fields.Add(new() { Name = "Items", Value = activeCount.ToString(), Inline = true });
        if (ratingCount > 0)
        {
            fields.Add(new() { Name = "Rating", Value = $"★ {ratingAvg:0.0} ({ratingCount})", Inline = true });
        }

        if (featured.Count > 0)
        {
            var lines = featured.Take(5).Select(f => $"⭐ **{f.Name}** — 🪙 {f.Price} {f.CurrencyCode}");
            fields.Add(new() { Name = "Featured", Value = Truncate(string.Join("\n", lines), 1024), Inline = false });
        }

        // Author = the shop name, linked. When there's no logo, prefix with the type's emoji so the card still has
        // a visual mark (the icon "subs in" for a missing shop image).
        var authorName = logoUrl is null && typeEmoji is { } e ? $"{e} {name}" : name;
        return new EmbedProperties
        {
            Author = new EmbedAuthorProperties { Name = authorName, Url = storefrontUrl, IconUrl = logoUrl },
            Description = string.IsNullOrWhiteSpace(description) ? "Browse this shop." : Truncate(description, 500),
            Color = ParseAccent(accentColor) ?? Accent,
            Fields = fields,
            Image = bannerUrl is { } b ? new EmbedImageProperties(b) : null, // banner = the big hero image
            Footer = new EmbedFooterProperties { Text = ownerName, IconUrl = ownerAvatarUrl },
            Timestamp = updatedAt,
        };
    }

    private static Color? ParseAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var h = hex.TrimStart('#');
        return h.Length == 6 && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? new Color(rgb)
            : null;
    }

    /// <summary>An order receipt card — the bot analogue of <c>OrderReceipt.razor</c>'s summary.</summary>
    public static EmbedProperties Order(ShopOrderDetail d, ulong guildId, string? webBaseUrl = null)
    {
        var fields = new List<EmbedFieldProperties>
        {
            new() { Name = "Status", Value = StatusLabel(d.Status), Inline = true },
            new() { Name = "Total", Value = $"🪙 {d.Amount} {d.CurrencyCode}", Inline = true },
        };

        if (d.Quantity > 1)
        {
            fields.Add(new() { Name = "Quantity", Value = d.Quantity.ToString(), Inline = true });
        }

        fields.Add(new() { Name = "Buyer", Value = $"<@{d.BuyerId}>{Rep(d.BuyerRatingAvg, d.BuyerRatingCount)}", Inline = true });
        fields.Add(new() { Name = "Seller", Value = $"<@{d.SellerId}>{Rep(d.SellerRatingAvg, d.SellerRatingCount)}", Inline = true });

        if (d.Status == ShopOrderStatus.OfferPending)
        {
            var who = d.OfferProposedBy == ShopOfferParty.Buyer ? "buyer's offer" : "seller's counter";
            fields.Add(new() { Name = "Current offer", Value = $"🪙 {d.Amount} {d.CurrencyCode} · {who}", Inline = false });
        }

        if (d.Status == ShopOrderStatus.Settled)
        {
            fields.Add(new() { Name = "Commission (burned)", Value = $"−{d.FeeAmount} {d.CurrencyCode}", Inline = true });
            fields.Add(new() { Name = "Net to seller", Value = $"{d.Net} {d.CurrencyCode}", Inline = true });
        }

        if (d.Status == ShopOrderStatus.Disputed && !string.IsNullOrWhiteSpace(d.DisputeReason))
        {
            fields.Add(new() { Name = "Dispute", Value = Truncate(d.DisputeReason!, 1000), Inline = false });
        }

        return new EmbedProperties
        {
            Title = $"🧾 {d.ItemName}",
            Url = WebOrderUrl(webBaseUrl, guildId, d.Id),
            Color = StatusColor(d.Status),
            Fields = fields,
            Timestamp = d.CreatedAt,
        };
    }

    /// <summary>A compact, button-less DM notice for a terminal/outcome moment (settled, refunded, rated…).</summary>
    public static EmbedProperties Outcome(ShopLifecycleMoment moment, string itemName, string detail, ulong guildId, Guid? orderId, string? webBaseUrl = null)
    {
        var (emoji, heading, color) = OutcomeStyle(moment);
        return new EmbedProperties
        {
            Title = $"{emoji} {heading}",
            Url = orderId is { } oid ? WebOrderUrl(webBaseUrl, guildId, oid) : null,
            Description = Truncate($"**{itemName}**\n{detail}", 1000),
            Color = color,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>A button-less DM notice that a listing was removed by a moderator (with the reason in the detail).</summary>
    public static EmbedProperties ListingRemoved(string listingName, string detail) =>
        new()
        {
            Title = "🚫 Listing removed",
            Description = Truncate($"**{listingName}**\n{detail}", 1000),
            Color = new Color(0xEF4444),
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static (string Emoji, string Heading, Color Color) OutcomeStyle(ShopLifecycleMoment m) => m switch
    {
        ShopLifecycleMoment.Settled => ("✅", "Order settled", new Color(0x22C55E)),
        ShopLifecycleMoment.Cancelled => ("↩️", "Order cancelled — refunded", new Color(0x3B82F6)),
        ShopLifecycleMoment.Refunded => ("↩️", "Refunded", new Color(0x3B82F6)),
        ShopLifecycleMoment.Arbitrated => ("⚖️", "Dispute resolved", new Color(0x6366F1)),
        ShopLifecycleMoment.OfferRejected => ("🚫", "Offer ended", new Color(0x4B5563)),
        ShopLifecycleMoment.Rated => ("⭐", "You were rated", new Color(0xF59E0B)),
        _ => ("ℹ️", "Order update", new Color(0x4B5563)),
    };

    private static string StatusLabel(ShopOrderStatus s) => s switch
    {
        ShopOrderStatus.PendingDelivery => "🟡 Awaiting confirmation",
        ShopOrderStatus.Delivered => "📦 Delivered — confirm receipt",
        ShopOrderStatus.Settled => "✅ Settled",
        ShopOrderStatus.Disputed => "⚖️ Disputed",
        ShopOrderStatus.Refunded => "↩️ Refunded",
        ShopOrderStatus.Cancelled => "🚫 Cancelled",
        ShopOrderStatus.OfferPending => "💬 Offer pending",
        ShopOrderStatus.OfferDeclined => "🚫 Offer ended",
        _ => s.ToString(),
    };

    private static Color StatusColor(ShopOrderStatus s) => s switch
    {
        ShopOrderStatus.Settled => new Color(0x22C55E),
        ShopOrderStatus.Disputed => new Color(0xEF4444),
        ShopOrderStatus.Refunded or ShopOrderStatus.Cancelled => new Color(0x3B82F6),
        ShopOrderStatus.OfferPending => new Color(0xF59E0B),
        _ => new Color(0x6366F1),
    };

    /// <summary>A compact reputation suffix ("\n★ 4.5 (3)") — empty until the subject has a revealed rating.</summary>
    private static string Rep(double avg, int count) => count == 0 ? "" : $"\n★ {avg:0.0} ({count})";

    private static string? WebUrl(string? baseUrl, ulong guildId, Guid listingId) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/guilds/{guildId}/shop/listing/{listingId}";

    private static string? WebOrderUrl(string? baseUrl, ulong guildId, Guid orderId) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/guilds/{guildId}/shop/orders/{orderId}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
