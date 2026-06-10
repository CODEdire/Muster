using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using NetCord;
using NetCord.Rest;

namespace Muster.Bot.Shop.Rendering;

/// <summary>
/// Builds the action components for an order receipt (My orders ephemeral + DM action cards), viewer-aware and
/// phase-aware — the bot analogue of <c>OrderReceipt.razor</c>. Pure (no Discord/IO) so it's unit-testable.
/// Authorization still happens on click (each button dispatches the same CQRS command the web/API use); this only
/// decides which actions to <i>offer</i> a given viewer in a given state.
///
/// custom_id scheme: <c>{prefix}:{guildId}:{orderId}[:{extra}]</c> — carries its own state so the buttons work in
/// a DM with no channel context.
/// </summary>
public static class ShopOrderComponentBuilder
{
    public const string OrderPick = "shoporder"; // select: open one of my orders
    public const string Confirm = "sconf";       // buyer: confirm receipt
    public const string Deliver = "sdeliv";      // seller/manager: mark delivered (two-step)
    public const string Cancel = "scancel";      // seller/manager: cancel + refund a pending order
    public const string Dispute = "sdisp";       // buyer/seller: open the dispute modal
    public const string DisputeModal = "sdispm";
    public const string Accept = "soacc";        // offer: accept current price
    public const string Counter = "socnt";       // offer: open the counter modal
    public const string CounterModal = "socntm";
    public const string Decline = "sodec";       // offer: end the negotiation
    public const string ArbPay = "sarbp";        // manager: resolve → pay seller
    public const string ArbRefund = "sarbr";     // manager: resolve → refund buyer
    public const string RatePick = "srate";      // settled: star select → rating modal
    public const string RateModal = "sratem";
    public const string CommentInput = "comment";
    public const string ReasonInput = "reason";

    public static string Id(string prefix, ulong guildId, Guid orderId, string? extra = null) =>
        extra is null ? $"{prefix}:{guildId}:{orderId}" : $"{prefix}:{guildId}:{orderId}:{extra}";

    /// <summary>The viewer's available actions on one order, by status/role. Empty = nothing to do right now.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Order(
        ulong guildId, ShopOrderDetail d, ulong viewerId, bool isManager, bool alreadyRated, DateTimeOffset now)
    {
        var isBuyer = d.BuyerId == viewerId;
        var isSeller = d.SellerId == viewerId;
        var rows = new List<IMessageComponentProperties>();

        // --- Offer negotiation ---
        if (d.Status == ShopOrderStatus.OfferPending)
        {
            // Whose turn: buyer-proposed → seller/manager respond; seller-counter → buyer responds.
            var canRespond = d.OfferProposedBy == ShopOfferParty.Buyer ? (isSeller || isManager) : isBuyer;
            if (canRespond)
            {
                rows.Add(new ActionRowProperties(new ButtonProperties[]
                {
                    new(Id(Accept, guildId, d.Id), "Accept", ButtonStyle.Success),
                    new(Id(Counter, guildId, d.Id), "Counter", ButtonStyle.Secondary),
                }));
            }

            if (isBuyer || isSeller || isManager)
            {
                rows.Add(new ActionRowProperties(new[] { new ButtonProperties(Id(Decline, guildId, d.Id), "End offer", ButtonStyle.Danger) }));
            }

            return rows;
        }

        // --- Dispute arbitration (manager) ---
        if (d.Status == ShopOrderStatus.Disputed && isManager)
        {
            rows.Add(new ActionRowProperties(new ButtonProperties[]
            {
                new(Id(ArbPay, guildId, d.Id), "Pay seller", ButtonStyle.Success),
                new(Id(ArbRefund, guildId, d.Id), "Refund buyer", ButtonStyle.Secondary),
            }));
        }

        // --- Rating (settled, window open, viewer is a party who hasn't rated) ---
        if (d.Status == ShopOrderStatus.Settled && d.RatingsEnabled && !d.RatingsClosed && !alreadyRated
            && (isBuyer || isSeller)
            && (d.RatingWindowClosesAt is not { } closes || now <= closes))
        {
            rows.Add(RateSelect(guildId, d.Id, isBuyer ? "the seller" : "the buyer"));
        }

        // --- Pending order actions ---
        if (d.Status is ShopOrderStatus.PendingDelivery or ShopOrderStatus.Delivered)
        {
            var buttons = new List<ButtonProperties>();
            if (isBuyer)
            {
                buttons.Add(new(Id(Confirm, guildId, d.Id), "Confirm receipt", ButtonStyle.Success));
            }

            if ((isSeller || isManager) && d.TwoStepDelivery && d.Status == ShopOrderStatus.PendingDelivery)
            {
                buttons.Add(new(Id(Deliver, guildId, d.Id), "Mark delivered", ButtonStyle.Primary));
            }

            if (isSeller || isManager)
            {
                buttons.Add(new(Id(Cancel, guildId, d.Id), "Cancel & refund", ButtonStyle.Danger));
            }

            if (isBuyer || isSeller)
            {
                buttons.Add(new(Id(Dispute, guildId, d.Id), "Dispute", ButtonStyle.Secondary));
            }

            if (buttons.Count > 0)
            {
                rows.Add(new ActionRowProperties(buttons));
            }
        }

        return rows;
    }

    /// <summary>The arbitration buttons for the mod-channel dispute alert (any clicker is authorized on dispatch).</summary>
    public static ActionRowProperties ArbitrateButtons(ulong guildId, Guid orderId) =>
        new(new ButtonProperties[]
        {
            new(Id(ArbPay, guildId, orderId), "Pay seller", ButtonStyle.Success),
            new(Id(ArbRefund, guildId, orderId), "Refund buyer", ButtonStyle.Secondary),
        });

    /// <summary>A 1–5★ select that opens the rating comment modal carrying the chosen star count.</summary>
    public static StringMenuProperties RateSelect(ulong guildId, Guid orderId, string subjectLabel)
    {
        var options = Enumerable.Range(1, 5).Select(n =>
            new StringMenuSelectOptionProperties(new string('★', n) + new string('☆', 5 - n), n.ToString()));
        return new StringMenuProperties(Id(RatePick, guildId, orderId), options) { Placeholder = $"Rate {subjectLabel}…" };
    }

    /// <summary>An order picker for the My-orders ephemeral (≤ 25 options).</summary>
    public static StringMenuProperties MyOrders(ulong guildId, ulong viewerId, IReadOnlyList<ShopOrderRow> orders)
    {
        var options = orders.Take(25).Select(o =>
        {
            var role = o.BuyerId == viewerId ? "Buying" : "Selling";
            return new StringMenuSelectOptionProperties(Trunc($"{o.ItemName} — {o.Status}", 100), o.Id.ToString())
            {
                Description = Trunc($"{role} · {o.Amount} {o.CurrencyCode}", 100),
            };
        });
        return new StringMenuProperties($"{OrderPick}:{guildId}", options) { Placeholder = "Open an order…" };
    }

    public static ModalProperties DisputeModalFor(ulong guildId, Guid orderId) =>
        new(Id(DisputeModal, guildId, orderId), "Raise a dispute", new IModalComponentProperties[]
        {
            new LabelProperties("What's wrong?",
                new TextInputProperties(ReasonInput, TextInputStyle.Paragraph) { Required = true, MaxLength = 1000, Placeholder = "Describe the problem for the shop manager." }),
        });

    public static ModalProperties CounterModalFor(ulong guildId, Guid orderId) =>
        new(Id(CounterModal, guildId, orderId), "Counter offer", new IModalComponentProperties[]
        {
            new LabelProperties("Your counter (whole number)",
                new TextInputProperties("amount", TextInputStyle.Short) { Required = true, MaxLength = 18, Placeholder = "e.g. 80" }),
        });

    public static ModalProperties RateModalFor(ulong guildId, Guid orderId, int stars) =>
        new(Id(RateModal, guildId, orderId, stars.ToString()), $"Rate {new string('★', stars)}", new IModalComponentProperties[]
        {
            new LabelProperties("Comment (optional)",
                new TextInputProperties(CommentInput, TextInputStyle.Paragraph) { Required = false, MaxLength = 1000, Placeholder = "How did it go?" }),
        });

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
