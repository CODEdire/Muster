using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop.Core;

/// <summary>Pure presentation helpers for the shop board — shared by the listing/store cards, the storefront, and
/// the board page. No instance state; data-dependent display lives on the read-service row records.</summary>
public static class ShopPresentation
{
    /// <summary>Friendly stock badge text for a listing tile.</summary>
    public static string StockLabel(int quantity) =>
        quantity <= 0 ? "Out of stock" : quantity <= 20 ? $"{quantity} left" : "In stock";

    /// <summary>A compact price-range label for an active price filter chip.</summary>
    public static string PriceRangeLabel(int? min, int? max) => (min, max) switch
    {
        (not null, not null) => $"{min}–{max}",
        (not null, null) => $"≥ {min}",
        (null, not null) => $"≤ {max}",
        _ => "price",
    };

    /// <summary>First letter of a name, for an avatar/logo fallback monogram.</summary>
    public static string Initial(string s) => s.Length > 0 ? s[..1].ToUpperInvariant() : "?";

    /// <summary>A store card is worth an expand toggle only when something is actually hidden at rest
    /// (extra featured items beyond the first two, or a long description).</summary>
    public static bool Expandable(ShopStoreRow s) => (s.Featured?.Count ?? 0) > 2 || (s.Description?.Length ?? 0) > 90;
}
