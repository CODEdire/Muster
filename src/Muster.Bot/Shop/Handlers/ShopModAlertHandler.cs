using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Muster.Bot.Shop.Rendering;
using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;
using NetCord.Gateway;
using NetCord.Rest;

namespace Muster.Bot.Shop.Handlers;

/// <summary>
/// Posts a shop <b>dispute</b> alert to the guild's configured mod channel. Runs only in the Bot host (it owns
/// the gateway/REST client); shop lifecycle events reach it over the cross-host transport (see
/// <see cref="MessageRouting"/>), so a dispute raised from any surface (web, API, bot) pings the moderators.
/// Best-effort — a missing channel or a deleted message is logged, not fatal.
/// </summary>
public static class ShopModAlertHandler
{
    public static async Task Handle(
        ShopLifecycleNotified e,
        GuildShopSettingsService settings,
        IShopReadService reads,
        GatewayClient client,
        IConfiguration config,
        ILogger<ShopModAlert> logger,
        CancellationToken ct)
    {
        // Only disputes go to the mod channel; other moments are handled elsewhere (DM push / board).
        if (e.Moment != ShopLifecycleMoment.Disputed)
        {
            return;
        }

        var cfg = await settings.GetAsync(e.GuildId, ct);
        if (cfg.ShopModChannelId == 0)
        {
            return; // no mod channel configured
        }

        // Rich embed + in-channel arbitrate buttons (Pay seller / Refund buyer) when we can resolve the order;
        // managers resolve straight from the alert (the buttons authorize the clicker on dispatch).
        var message = new MessageProperties();
        var order = e.OrderId is { } oid ? await reads.GetOrderAsync(e.GuildId, oid, ct) : null;
        if (order is not null)
        {
            message.Embeds = [ShopEmbedRenderer.Order(order, e.GuildId, config["Web:BaseUrl"])];
            message.Components = [ShopOrderComponentBuilder.ArbitrateButtons(e.GuildId, order.Id)];
            message.Content = "⚖️ **New shop dispute** — resolve below:";
        }
        else
        {
            var url = $"{config["Web:BaseUrl"]}/guilds/{e.GuildId}/shop/disputes";
            message.Content = $"⚖️ **Shop dispute** — **{e.ListingName}**\n{e.Detail}\nArbitrate: {url}";
        }

        try
        {
            await client.Rest.SendMessageAsync(cfg.ShopModChannelId, message, cancellationToken: ct);
        }
        catch (RestException ex)
        {
            logger.LogWarning(ex, "Could not post shop dispute alert to channel {ChannelId} in guild {GuildId}.", cfg.ShopModChannelId, e.GuildId);
        }
    }
}

/// <summary>Logger category marker for <see cref="ShopModAlertHandler"/> (which is static).</summary>
public sealed class ShopModAlert;
