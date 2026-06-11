using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Shops;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop.Components;

public partial class ShopItemModal
{
    [Parameter, EditorRequired] public ulong GuildId { get; set; }
    [Parameter, EditorRequired] public Guid ItemId { get; set; }
    [Parameter, EditorRequired] public ulong UserId { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    /// <summary>Raised after an action that changes the listing (withdraw/feature/take-down) so the parent reloads.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private ShopListingDetail? _listing;
    private bool _isSeller, _isManager, _acting, _loaded;
    private long _offerAmount;
    private int _buyQty = 1;
    private string? _message;
    private Guid _loadedId;

    protected override async Task OnParametersSetAsync()
    {
        if (ItemId != _loadedId)
        {
            _loadedId = ItemId;
            _message = null;
            _offerAmount = 0;
            _buyQty = 1;
            await ReloadAsync();
        }
    }

    private void IncQty() { if (_listing is { } l && _buyQty < l.Quantity) { _buyQty++; } }
    private void DecQty() { if (_buyQty > 1) { _buyQty--; } }

    private async Task ReloadAsync()
    {
        _loaded = false;
        await using var scope = Scopes.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _listing = await reads.GetListingDetailAsync(GuildId, ItemId);
        _loaded = true;
        if (_listing is not null)
        {
            _isSeller = _listing.SellerId == UserId;
            _isManager = await scope.ServiceProvider
                .GetRequiredService<Muster.Infrastructure.Services.Membership.GuildAuthorizationService>()
                .IsShopManagerAsync(GuildId, UserId);
            // Keep the chosen quantity within current stock (it can drop while the modal is open).
            _buyQty = Math.Clamp(_buyQty, 1, Math.Max(1, _listing.Quantity));
        }
    }

    private Task CloseAsync() => OnClose.InvokeAsync();

    private async Task<T?> RunAsync<T>(Func<Wolverine.IMessageBus, Task<T>> action) where T : class
    {
        await using var scope = Scopes.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>());
    }

    private async Task BuyAsync()
    {
        if (_acting) { return; }
        _acting = true;
        try
        {
            var qty = Math.Clamp(_buyQty, 1, Math.Max(1, _listing?.Quantity ?? 1));
            var result = await RunAsync(bus => bus.InvokeAsync<Result<Guid>>(new PurchaseListing(GuildId, UserId, ItemId, qty)));
            _message = result!.Ok
                ? "Purchased — funds are held in escrow until you confirm receipt. See My orders."
                : ((Result)result).ToCommandResult("").Message;
        }
        finally { _acting = false; await ReloadAsync(); await OnChanged.InvokeAsync(); }
    }

    private async Task MakeOfferAsync()
    {
        if (_acting || _offerAmount <= 0) { return; }
        _acting = true;
        try
        {
            var result = await RunAsync(bus => bus.InvokeAsync<Result<Guid>>(new MakeOffer(GuildId, UserId, ItemId, _offerAmount)));
            _message = result!.Ok
                ? "Offer sent — funds are held until the seller responds. See My orders."
                : ((Result)result).ToCommandResult("").Message;
            if (result.Ok) { _offerAmount = 0; }
        }
        finally { _acting = false; await ReloadAsync(); }
    }

    private async Task FeatureAsync()
    {
        if (_acting) { return; }
        _acting = true;
        try
        {
            var result = await RunAsync(bus => bus.InvokeAsync<Result>(new FeatureListing(GuildId, UserId, ItemId)));
            _message = result!.ToCommandResult("Listing featured — promoted in the shop channel.").Message;
        }
        finally { _acting = false; await ReloadAsync(); await OnChanged.InvokeAsync(); }
    }

    private async Task UnfeatureAsync()
    {
        if (_acting) { return; }
        _acting = true;
        try
        {
            var result = await RunAsync(bus => bus.InvokeAsync<Result>(new UnfeatureListing(GuildId, UserId, ItemId)));
            _message = result!.ToCommandResult("Listing un-featured (fee not refunded).").Message;
        }
        finally { _acting = false; await ReloadAsync(); await OnChanged.InvokeAsync(); }
    }

    private async Task CancelAsync()
    {
        if (_acting || !await JS.InvokeAsync<bool>("confirm", new object?[] { "Withdraw this listing?" })) { return; }
        _acting = true;
        try
        {
            var result = await RunAsync(bus => bus.InvokeAsync<Result>(new CancelListing(GuildId, UserId, ItemId)));
            _message = result!.ToCommandResult("Listing withdrawn.").Message;
        }
        finally { _acting = false; await ReloadAsync(); await OnChanged.InvokeAsync(); }
    }

    private async Task TakeDownAsync()
    {
        if (_acting) { return; }
        var reason = await JS.InvokeAsync<string?>("prompt", new object?[] { "Reason for taking this listing down (shown to the seller):" });
        if (reason is null) { return; }
        _acting = true;
        try
        {
            var result = await RunAsync(bus => bus.InvokeAsync<Result>(new CancelListing(GuildId, UserId, ItemId, reason)));
            _message = result!.ToCommandResult("Listing taken down.").Message;
        }
        finally { _acting = false; await ReloadAsync(); await OnChanged.InvokeAsync(); }
    }
}
