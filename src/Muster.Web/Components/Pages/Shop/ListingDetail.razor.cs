using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop;

public partial class ListingDetail
{
    [Parameter] public Guid ListingId { get; set; }

    private bool _notFound;

    protected override async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        var listing = await reads.GetListingDetailAsync(GuildId, ListingId);
        if (listing is null)
        {
            _notFound = true;
            return;
        }

        Nav.NavigateTo($"/guilds/{GuildId}/shop/store/{listing.StoreSlug}?item={ListingId}", forceLoad: false, replace: true);
    }
}
