using Microsoft.AspNetCore.Components;

namespace Muster.Web.Components.Pages.Shop;

public partial class ShopDisputes
{
    [SupplyParameterFromQuery(Name = "store")] private string? Store { get; set; }

    protected override Task LoadAsync()
    {
        var qs = string.IsNullOrEmpty(Store) ? "" : $"&store={Store}";
        Nav.NavigateTo($"/guilds/{GuildId}/shop/orders?tab=disputes{qs}", forceLoad: false, replace: true);
        return Task.CompletedTask;
    }
}
