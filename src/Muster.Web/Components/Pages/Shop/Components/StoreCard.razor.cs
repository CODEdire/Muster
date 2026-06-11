using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop.Components;

public partial class StoreCard
{
    [Parameter, EditorRequired] public ShopStoreRow Store { get; set; } = default!;

    [Parameter] public ulong GuildId { get; set; }

    /// <summary>Whether the viewer can edit this shop (a shop manager, or the owner with the creator tier) — shows
    /// the inline edit affordance in the card footer.</summary>
    [Parameter] public bool CanManage { get; set; }

    // Expand/collapse is per-card and self-contained, so the card owns it rather than the board page.
    private bool _expanded;
    private void Toggle() => _expanded = !_expanded;
}
