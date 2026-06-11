using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop.Components;

public partial class ListingCard
{
    [Parameter, EditorRequired] public ShopListingCard Listing { get; set; } = default!;

    /// <summary>URL the tile's thumbnail and title link to. The board opens the item modal via a query param; a
    /// storefront uses its own store-scoped URL — the page decides, the card just links.</summary>
    [Parameter, EditorRequired] public string ItemHref { get; set; } = "";

    /// <summary>Guild id for the overlay storefront link — only used when <see cref="ShowStoreChip"/> is set.</summary>
    [Parameter] public ulong GuildId { get; set; }

    /// <summary>Show the storefront chip in the media overlay. The market board sets this; a storefront page
    /// (already scoped to one store) leaves it off.</summary>
    [Parameter] public bool ShowStoreChip { get; set; }

    /// <summary>Right-hand content of the meta row — the market shows the seller chip, a storefront a Details link.</summary>
    [Parameter] public RenderFragment? Trailing { get; set; }
}
