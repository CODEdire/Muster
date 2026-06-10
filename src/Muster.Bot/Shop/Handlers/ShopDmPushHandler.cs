using Microsoft.Extensions.Configuration;
using Muster.Bot.Shop.Modules;
using Muster.Bot.Shop.Rendering;
using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Muster.Bot.Shop.Handlers;

/// <summary>
/// DMs the person a shop order moment is <i>about</i> (the event's <c>TargetUserId</c>), so they don't have to
/// open the web receipt. Two kinds, mirroring the quest DM handler:
/// <list type="bullet">
/// <item><b>Action cards</b> (purchased / delivered / offer made-countered-accepted) — the order receipt embed +
/// the recipient's viewer-appropriate buttons, so they can act straight from the DM.</item>
/// <item><b>Outcome notices</b> (settled / cancelled / refunded / offer ended / arbitrated / rated) — a light,
/// button-less summary closing the loop.</item>
/// </list>
/// Best-effort — silently skips if DMs are closed. Bot-host only (owns the gateway); shop events arrive over the
/// cross-host transport. Disputes have no DM target (they route to the mod channel via <see cref="ShopModAlertHandler"/>).
/// </summary>
public static class ShopDmPushHandler
{
    private static readonly HashSet<ShopLifecycleMoment> ActionMoments =
    [
        ShopLifecycleMoment.Purchased,
        ShopLifecycleMoment.Delivered,
        ShopLifecycleMoment.OfferMade,
        ShopLifecycleMoment.OfferCountered,
        ShopLifecycleMoment.OfferAccepted,
    ];

    private static readonly HashSet<ShopLifecycleMoment> OutcomeMoments =
    [
        ShopLifecycleMoment.Settled,
        ShopLifecycleMoment.Cancelled,
        ShopLifecycleMoment.Refunded,
        ShopLifecycleMoment.OfferRejected,
        ShopLifecycleMoment.Arbitrated,
        ShopLifecycleMoment.Rated,
    ];

    public static async Task Handle(
        ShopLifecycleNotified e,
        GatewayClient client,
        IShopReadService reads,
        Muster.Infrastructure.Services.Membership.GuildAuthorizationService authz,
        IConfiguration config,
        CancellationToken ct)
    {
        if (e.TargetUserId is not { } target)
        {
            return;
        }

        // Listing-level takedown (no order): notify the seller a moderator removed their listing.
        if (e.OrderId is not { } orderId)
        {
            if (e.Moment == ShopLifecycleMoment.Cancelled)
            {
                await TrySendAsync(client, target, ShopEmbedRenderer.ListingRemoved(e.ListingName, e.Detail), []);
            }

            return;
        }

        if (ActionMoments.Contains(e.Moment))
        {
            // Render the receipt from the recipient's perspective so the buttons are theirs to use.
            var view = await ShopHub.OrderAsync(reads, authz, config["Web:BaseUrl"], e.GuildId, orderId, target);
            if (view is { } v)
            {
                await TrySendAsync(client, target, v.Embed, v.Components);
            }

            return;
        }

        if (OutcomeMoments.Contains(e.Moment))
        {
            var notice = ShopEmbedRenderer.Outcome(e.Moment, e.ListingName, e.Detail, e.GuildId, orderId, config["Web:BaseUrl"]);
            await TrySendAsync(client, target, notice, []);
        }
    }

    private static async Task<bool> TrySendAsync(GatewayClient client, ulong userId, EmbedProperties embed, IReadOnlyList<IMessageComponentProperties> components)
    {
        try
        {
            var dm = await client.Rest.GetDMChannelAsync(userId);
            await dm.SendMessageAsync(new MessageProperties { Embeds = [embed], Components = components });
            return true;
        }
        catch (RestException)
        {
            return false; // DMs closed — best-effort
        }
    }
}
