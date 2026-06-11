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

    private string Money(long n) => $"{n} {_order!.CurrencyCode}";
    private string OriginLabel => _order!.IsGuildOrder ? "guild order" : "member order";

    private enum StepState { Done, Current, Pending, Bad }
    private sealed record TimelineStep(string Icon, string Title, DateTimeOffset? At, DateTimeOffset? Deadline, string Note, StepState State);

    // A short relative countdown for the one live deadline shown on the current step.
    private static string Relative(DateTimeOffset when)
    {
        var span = when - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero) { return "due now"; }
        if (span.TotalDays >= 1) { return $"{(int)span.TotalDays}d {span.Hours}h left"; }
        if (span.TotalHours >= 1) { return $"{(int)span.TotalHours}h {span.Minutes}m left"; }
        return $"{Math.Max(1, (int)span.TotalMinutes)}m left";
    }

    // Header status pill: friendly text + tone class + Material icon.
    private (string Text, string Tone, string Icon) HeaderStatus() => _order!.Status switch
    {
        ShopOrderStatus.PendingDelivery => (_order.TwoStepDelivery ? "awaiting delivery" : "awaiting confirmation", "info", "hourglass_top"),
        ShopOrderStatus.Delivered => ("awaiting confirmation", "info", "hourglass_top"),
        ShopOrderStatus.Settled => ("settled", "ok", "check_circle"),
        ShopOrderStatus.Disputed => ("disputed", "bad", "gavel"),
        ShopOrderStatus.Refunded => ("refunded", "bad", "undo"),
        ShopOrderStatus.Cancelled => ("cancelled", "bad", "undo"),
        ShopOrderStatus.OfferPending => ("offer pending", "warn", "swap_horiz"),
        ShopOrderStatus.OfferDeclined => ("offer ended", "bad", "cancel"),
        _ => (_order.Status.ToString().ToLowerInvariant(), "info", "receipt_long"),
    };

    // The escrow line under the total: where the held funds stand.
    private (string Text, string Icon) Escrow() => _order!.Status switch
    {
        ShopOrderStatus.Settled => (_order.IsGuildOrder ? "payment burned" : $"released to {_order.SellerName}", "check_circle"),
        ShopOrderStatus.Refunded or ShopOrderStatus.Cancelled or ShopOrderStatus.OfferDeclined => ($"refunded to {_order.BuyerName}", "undo"),
        ShopOrderStatus.Disputed => ("held · in dispute", "lock"),
        _ => ("held in escrow", "lock"),
    };

    // The viewer-aware order journey, synthesised from the order's discrete timestamps + status.
    private IReadOnlyList<TimelineStep> Timeline()
    {
        var o = _order!;
        var b = o.BuyerName;
        var s = o.SellerName;
        var steps = new List<TimelineStep>();

        if (o.Status is ShopOrderStatus.OfferPending or ShopOrderStatus.OfferDeclined)
        {
            var declined = o.Status == ShopOrderStatus.OfferDeclined;
            steps.Add(new("local_offer", "Offer placed", o.CreatedAt, null, $"{b} offered {Money(o.Amount)}.", StepState.Done));
            steps.Add(new(declined ? "cancel" : "swap_horiz", declined ? "Offer ended" : "Negotiating",
                null, declined ? null : o.OfferExpiresAt,
                declined ? $"Funds refunded to {b}."
                         : $"{(o.OfferProposedBy == ShopOfferParty.Buyer ? s : b)}'s turn to respond.",
                declined ? StepState.Bad : StepState.Current));
            if (!declined)
            {
                steps.Add(new("check_circle", "Accepted", null, null, "Converts to an order; funds stay held.", StepState.Pending));
                steps.Add(new("paid", "Settled", null, null, "Released after delivery and confirmation.", StepState.Pending));
            }

            return steps;
        }

        steps.Add(new("shopping_cart", "Ordered", o.CreatedAt, null,
            o.FromOffer ? $"{b}'s offer accepted at {Money(o.Amount)}; paid into escrow."
                        : $"{b} paid {Money(o.Amount)} into escrow.", StepState.Done));

        if (o.TwoStepDelivery)
        {
            var delivered = o.DeliveredAt is not null;
            steps.Add(new("local_shipping", "Delivered", o.DeliveredAt, null,
                delivered ? $"{s} marked the order delivered."
                          : (_isSeller ? "Mark the order delivered." : $"Awaiting delivery from {s}."),
                delivered ? StepState.Done : StepState.Current));
        }

        switch (o.Status)
        {
            case ShopOrderStatus.Settled:
                steps.Add(new("paid", "Settled", o.SettledAt, null,
                    o.IsGuildOrder ? $"{b} confirmed; {Money(o.Amount)} burned (guild order)."
                                   : $"Confirmed; {Money(o.Net)} released to {s}, {Money(o.FeeAmount)} burned.", StepState.Done));
                break;
            case ShopOrderStatus.Cancelled:
                steps.Add(new("undo", "Cancelled", null, null, $"{s} cancelled — {b} refunded.", StepState.Bad));
                break;
            case ShopOrderStatus.Refunded:
                steps.Add(new("undo", "Refunded", null, null,
                    $"{b} refunded{(o.ResolvedByName is { } r ? $" · resolved by {r}" : "")}.", StepState.Bad));
                break;
            case ShopOrderStatus.Disputed:
                steps.Add(new("gavel", "Disputed", o.DisputeRaisedAt, o.DisputeAutoResolveAt,
                    $"Raised by {(o.DisputedBy == o.BuyerId ? b : s)}.", StepState.Bad));
                steps.Add(new("balance", "Resolution", null, null,
                    _isManager ? "Pay the seller or refund the buyer." : "A shop manager will resolve this.", StepState.Pending));
                return steps;
            default: // PendingDelivery / Delivered awaiting confirmation
                // Under two-step delivery the buyer can't confirm until the seller delivers, so the confirm step
                // only goes "current" (with its auto-settle countdown) once delivery has happened.
                var awaitingDelivery = o.TwoStepDelivery && o.Status == ShopOrderStatus.PendingDelivery;
                steps.Add(new("check_circle", "Confirm receipt", null, awaitingDelivery ? null : o.AutoSettleAt,
                    _isSeller ? $"Awaiting confirmation from {b}."
                              : (o.IsGuildOrder ? "Confirm to complete the order." : $"Confirm to release {Money(o.Net)} to {s}."),
                    awaitingDelivery ? StepState.Pending : StepState.Current));
                steps.Add(new("paid", "Settled", null, null,
                    o.IsGuildOrder ? "Payment is burned on completion." : $"{Money(o.Net)} to {s}, {Money(o.FeeAmount)} burned.", StepState.Pending));
                break;
        }

        if (o.RatingsEnabled && !o.IsGuildOrder)
        {
            if (o.Status == ShopOrderStatus.Settled)
            {
                steps.Add(new("star", "Rate each other", null, o.RatingsClosed ? null : o.RatingWindowClosesAt,
                    o.RatingsClosed ? "Ratings revealed." : "Blind until you both submit.",
                    o.RatingsClosed ? StepState.Done : StepState.Current));
            }
            else if (o.Status is ShopOrderStatus.PendingDelivery or ShopOrderStatus.Delivered)
            {
                steps.Add(new("star", "Rate", null, null, "Opens once the order settles.", StepState.Pending));
            }
        }

        return steps;
    }

    private static string StepClass(StepState s) => s switch
    {
        StepState.Done => "done",
        StepState.Current => "current",
        StepState.Bad => "bad",
        _ => "pending",
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
