using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Muster.Bot.Shop.Rendering;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Infrastructure.Services.Shops;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord.Gateway;
using NetCord.Rest;

namespace Muster.Bot.Shop.Handlers;

/// <summary>
/// Maintains the shop channel's <b>one card per open store</b> — the store hero card (logo, owner, rating, item
/// count, and the store's featured items inline + quick-pick). Runs in the Bot host (owns the gateway/REST). This
/// is the only persistent shop presence in a channel; the long tail of listings lives in ephemeral browse, so the
/// channel never floods as a store grows. Maintained on <see cref="ShopStoreChanged"/> (store create/edit/delete)
/// and refreshed when a store's listings change (<see cref="ShopLifecycleNotified"/>). Idempotent + self-healing.
/// </summary>
public static class ShopBoardNotificationHandler
{
    public const string HomeType = "shop-home";

    // Listing moments that change a store's hero card (item count / rating / featured set).
    private static readonly HashSet<ShopLifecycleMoment> HeroRefreshMoments =
    [
        ShopLifecycleMoment.Listed,
        ShopLifecycleMoment.Cancelled,
        ShopLifecycleMoment.Expired,
        ShopLifecycleMoment.Purchased,
        ShopLifecycleMoment.Settled,
        ShopLifecycleMoment.Refunded,
        ShopLifecycleMoment.Rated,
        ShopLifecycleMoment.Featured,
        ShopLifecycleMoment.Unfeatured,
        ShopLifecycleMoment.Restocked,
    ];

    public static async Task Handle(
        ShopLifecycleNotified e,
        MusterDbContext db,
        GuildShopSettingsService settings,
        IShopReadService reads,
        GatewayClient client,
        IConfiguration config,
        ILogger<ShopBoardNotification> logger,
        CancellationToken ct)
    {
        if (!HeroRefreshMoments.Contains(e.Moment))
        {
            return;
        }

        var storeId = await db.ShopListings.AsNoTracking().Where(l => l.Id == e.ListingId).Select(l => (Guid?)l.StoreId).FirstOrDefaultAsync(ct);
        if (storeId is { } sid)
        {
            await EnsureStoreHeroAsync(db, settings, reads, client, config, e.GuildId, sid, logger, ct);
        }
    }

    internal static async Task EnsureStoreHeroAsync(
        MusterDbContext db, GuildShopSettingsService settings, IShopReadService reads, GatewayClient client,
        IConfiguration config, ulong guildId, Guid storeId, ILogger logger, CancellationToken ct)
    {
        var cfg = await settings.GetAsync(guildId, ct);
        var store = await db.FindStoreInGuildAsync(guildId, storeId, ct);
        var existing = await db.FindPostedMessageAsync(HomeType, storeId, ct);

        var show = store is { Closed: false } && cfg.ShopChannelId != 0;
        if (!show)
        {
            await RemoveAsync(db, client, existing, logger, ct);
            return;
        }

        var count = await db.CountActiveListingsInStoreAsync(storeId, ct);
        var (avg, ratingCount) = await db.RatingSummaryAsync(guildId, RatingContext.ShopOrder, store!.OwnerId, RatingRole.Provider, ct);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == store.OwnerId)
            .Select(u => new { Name = u.GlobalName ?? u.Username, u.AvatarHash }).FirstOrDefaultAsync(ct);
        var featured = await reads.GetFeaturedForStoreAsync(guildId, storeId, ct);
        var type = store.StoreTypeId is { } tid
            ? await db.ShopStoreTypes.AsNoTracking().Where(t => t.Id == tid).Select(t => new { t.Name, t.Icon }).FirstOrDefaultAsync(ct)
            : null;

        var baseUrl = config["Web:BaseUrl"];
        string? Img(string? key) => key is { } k && !string.IsNullOrWhiteSpace(baseUrl) ? $"{baseUrl!.TrimEnd('/')}/shop-image/{k}" : null;
        var storefrontUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl!.TrimEnd('/')}/guilds/{guildId}/shop/store/{store.Slug}";
        var ownerAvatar = Muster.Infrastructure.Discord.DiscordCdn.AvatarUrl(store.OwnerId, owner?.AvatarHash);

        var embed = ShopEmbedRenderer.StoreHero(
            store.Name, type?.Name, Muster.Contracts.ShopIcons.Emoji(type?.Icon), store.Description,
            owner?.Name ?? store.OwnerId.ToString(), ownerAvatar, count, avg, ratingCount,
            Img(store.LogoImageKey), Img(store.BannerImageKey), store.AccentColor, featured, storefrontUrl, DateTimeOffset.UtcNow);
        var components = ShopComponentBuilder.StoreHero(guildId, storeId, featured);
        await UpsertAsync(db, client, storeId, guildId, cfg.ShopChannelId, existing, embed, components, logger, ct);
    }

    private static async Task UpsertAsync(
        MusterDbContext db, GatewayClient client, Guid storeId, ulong guildId, ulong target, PostedMessage? existing,
        EmbedProperties embed, IReadOnlyList<IMessageComponentProperties> components, ILogger logger, CancellationToken ct)
    {
        if (existing is not null && existing.ChannelId == target)
        {
            try
            {
                await client.Rest.ModifyMessageAsync(target, existing.MessageId, m => { m.Embeds = [embed]; m.Components = components; }, cancellationToken: ct);
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (RestException ex)
            {
                logger.LogWarning(ex, "Shop hero card {MessageId} could not be edited; re-posting.", existing.MessageId);
            }
        }
        else if (existing is not null)
        {
            await TryDeleteAsync(client, existing.ChannelId, existing.MessageId, logger, ct);
        }

        var sent = await client.Rest.SendMessageAsync(target, new MessageProperties { Embeds = [embed], Components = components }, cancellationToken: ct);

        if (existing is null)
        {
            db.PostedMessages.Add(new PostedMessage
            {
                Id = Guid.NewGuid(),
                GuildId = guildId,
                EntityType = HomeType,
                EntityId = storeId,
                ChannelId = target,
                MessageId = sent.Id,
                EverPublic = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.ChannelId = target;
            existing.MessageId = sent.Id;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task RemoveAsync(MusterDbContext db, GatewayClient client, PostedMessage? existing, ILogger logger, CancellationToken ct)
    {
        if (existing is null)
        {
            return;
        }

        await TryDeleteAsync(client, existing.ChannelId, existing.MessageId, logger, ct);
        await db.PostedMessages.Where(p => p.Id == existing.Id).ExecuteDeleteAsync(ct);
    }

    private static async Task TryDeleteAsync(GatewayClient client, ulong channelId, ulong messageId, ILogger logger, CancellationToken ct)
    {
        try
        {
            await client.Rest.DeleteMessageAsync(channelId, messageId, cancellationToken: ct);
        }
        catch (RestException ex)
        {
            logger.LogWarning(ex, "Shop hero card {MessageId} could not be deleted (already gone?).", messageId);
        }
    }
}

/// <summary>Renders/removes a store's hero card on store create/edit/delete. Separate class from the listing-event
/// handler so Wolverine discovers both message handlers unambiguously (one Handle per class).</summary>
public static class ShopStoreCardHandler
{
    public static Task Handle(
        ShopStoreChanged e,
        MusterDbContext db,
        GuildShopSettingsService settings,
        IShopReadService reads,
        GatewayClient client,
        IConfiguration config,
        ILogger<ShopBoardNotification> logger,
        CancellationToken ct)
        => ShopBoardNotificationHandler.EnsureStoreHeroAsync(db, settings, reads, client, config, e.GuildId, e.StoreId, logger, ct);
}

/// <summary>Logger category marker for <see cref="ShopBoardNotificationHandler"/> (which is static).</summary>
public sealed class ShopBoardNotification;
