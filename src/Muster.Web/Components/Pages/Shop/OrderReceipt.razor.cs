using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Entities.Shops;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Shops;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Web.Components.Pages.Shop;

public partial class OrderReceipt
{
    [Parameter] public Guid OrderId { get; set; }

    private ShopOrderDetail? _order;
    private FeatureVerdict _shopGate;
    private ShopOrderRatings? _ratings;
    private bool _canView, _isBuyer, _isSeller, _isManager, _busy;
    private bool _showDispute, _imagesAvailable, _uploading;
    private string? _disputeReason, _disputeEvidence;
    private long _counterAmount;
    private int _rateStars = 5;
    private string? _rateComment;
    private string _zoneId = Muster.Infrastructure.Services.Platform.TimeZoneService.Utc;

    private string ShortId => OrderId.ToString("N")[..8];
    private bool IsPending => _order?.Status is ShopOrderStatus.PendingDelivery or ShopOrderStatus.Delivered;

    // Under two-step delivery the buyer can only confirm after the seller marks the order delivered.
    private bool CanConfirm => _order is { } o
        && (o.Status == ShopOrderStatus.Delivered || (!o.TwoStepDelivery && o.Status == ShopOrderStatus.PendingDelivery));

    // It's the viewer's turn to respond: seller/manager when the buyer proposed; buyer when the seller countered.
    private bool CanRespond => _order is { Status: ShopOrderStatus.OfferPending } o
        && (o.OfferProposedBy == ShopOfferParty.Buyer ? (_isSeller || _isManager) : _isBuyer);

    // A buyer/seller may rate while the order is settled, ratings are on, the window is open, and they haven't yet.
    private bool CanRate => _order is { Status: ShopOrderStatus.Settled, RatingsEnabled: true, RatingsClosed: false }
        && (_isBuyer || _isSeller) && _ratings?.Mine is null
        && (_order.RatingWindowClosesAt is not { } w || DateTimeOffset.UtcNow <= w);

    private static string Stars(int n) => new string('★', Math.Clamp(n, 0, 5)) + new string('☆', 5 - Math.Clamp(n, 0, 5));

    protected override async Task LoadAsync()
    {
        await using (var scope = Scopes.CreateAsyncScope())
        {
            _shopGate = await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Platform.IFeatureGate>()
                .EvaluateAsync(GuildId, PlatformFeature.Shop);
        }
        if (!_shopGate.CanEnable) { return; } // platform/plan block — render the notice, load nothing

        _zoneId = await TimeZones.ResolveZoneIdAsync(GuildId, UserId);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IShopReadService>();
        _order = await reads.GetOrderAsync(GuildId, OrderId);
        if (_order is null)
        {
            return;
        }

        _isBuyer = _order.BuyerId == UserId;
        _isSeller = _order.SellerId == UserId;
        _isManager = await Auth.IsShopManagerAsync(GuildId, UserId);
        _canView = _isBuyer || _isSeller || _isManager;
        _imagesAvailable = scope.ServiceProvider.GetService<IShopImageService>() is not NoOpShopImageService and not null;

        _ratings = _order.Status == ShopOrderStatus.Settled && _order.RatingsEnabled
            ? await reads.GetOrderRatingsAsync(GuildId, OrderId, UserId)
            : null;
    }

    private async Task SubmitRatingAsync()
    {
        if (_busy || _rateStars < 1)
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new RateOrder(GuildId, UserId, OrderId, _rateStars, _rateComment)))
                .ToCommandResult("Thanks — your rating is in (hidden until revealed).").Message;
            _rateComment = null;
            _rateStars = 5;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task HideRatingAsync(Guid ratingId)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new ModerateRating(GuildId, UserId, ratingId, true)))
                .ToCommandResult("Rating hidden.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task OnEvidenceSelectedAsync(InputFileChangeEventArgs e)
    {
        _uploading = true;
        try
        {
            const long max = 20 * 1024 * 1024;
            await using var stream = e.File.OpenReadStream(max);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            await using var scope = Scopes.CreateAsyncScope();
            var images = scope.ServiceProvider.GetRequiredService<IShopImageService>();
            var (result, key) = await images.UploadAsync(ms, ms.Length, e.File.ContentType, ShopImageKind.Listing);
            if (result == ShopImageUploadResult.Ok) { _disputeEvidence = key; }
        }
        catch { /* evidence is optional; ignore upload failure */ }
        finally { _uploading = false; }
    }

    private async Task SubmitDisputeAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_disputeReason))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new DisputeOrder(GuildId, UserId, OrderId, _disputeReason!.Trim(), _disputeEvidence)))
                .ToCommandResult("Dispute raised — a shop manager will review it.").Message;
            _showDispute = false;
            _disputeReason = null;
            _disputeEvidence = null;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task ArbitrateAsync(bool paySeller)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new ArbitrateOrder(GuildId, UserId, OrderId, paySeller)))
                .ToCommandResult(paySeller ? "Resolved — seller paid." : "Resolved — buyer refunded.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task MarkDeliveredAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new MarkDelivered(GuildId, UserId, OrderId))).ToCommandResult("Marked delivered.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private Task AcceptOfferAsync() => OfferAsync(new AcceptOffer(GuildId, UserId, OrderId), "Offer accepted — awaiting delivery confirmation.");
    private Task DeclineOfferAsync() => OfferAsync(new DeclineOffer(GuildId, UserId, OrderId), "Offer ended — buyer refunded.");

    private async Task CounterAsync()
    {
        if (_busy || _counterAmount <= 0)
        {
            return;
        }

        await OfferAsync(new CounterOffer(GuildId, UserId, OrderId, _counterAmount), $"Countered at {_counterAmount}.");
        _counterAmount = 0;
    }

    private async Task OfferAsync(IGuildCommand command, string ok)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(command)).ToCommandResult(ok).Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private static string StatusChip(ShopOrderStatus s) => s switch
    {
        ShopOrderStatus.Settled => "chip-review",
        ShopOrderStatus.Cancelled or ShopOrderStatus.Refunded => "chip-closed",
        ShopOrderStatus.Disputed => "chip-closed",
        _ => "chip-progress",
    };

    private async Task ConfirmAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new ConfirmReceipt(GuildId, UserId, OrderId))).ToCommandResult("Receipt confirmed — funds released to the seller.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }

    private async Task CancelAsync()
    {
        if (_busy || _order is null
            || !await JS.InvokeAsync<bool>("confirm", new object?[] { $"Cancel the order for “{_order.ItemName}” and refund the buyer?" }))
        {
            return;
        }

        _busy = true;
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            Message = (await bus.InvokeAsync<Result>(new SellerCancelOrder(GuildId, UserId, OrderId))).ToCommandResult("Order cancelled — buyer refunded.").Message;
        }
        finally { _busy = false; await ReloadAsync(); }
    }
}
