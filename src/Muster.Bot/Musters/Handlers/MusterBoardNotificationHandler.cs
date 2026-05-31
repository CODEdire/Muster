using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Muster.Bot.Musters.Rendering;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Musters;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord.Gateway;
using NetCord.Rest;

namespace Muster.Bot.Musters.Handlers;

/// <summary>
/// Renders a muster's live card into its channel. Runs in the Bot host (it owns the gateway/REST client). One card
/// per muster, edited in place as members check in; reuses the shared <see cref="PostedMessage"/> board machinery
/// (EntityType "muster"). Idempotent + self-healing: each event re-reads current state and edits the tracked message,
/// re-posting if it was deleted out from under us. A closed/expired card stays until the cleanup sweep prunes it.
/// </summary>
public static class MusterBoardNotificationHandler
{
    public const string EntityType = "muster";

    public static async Task Handle(
        MusterChanged e,
        MusterDbContext db,
        GuildMusterSettingsService musterSettings,
        GatewayClient client,
        IConfiguration config,
        ILogger<MusterBoardNotification> logger,
        CancellationToken ct)
    {
        var muster = await db.FindMusterAsync(e.MusterId, ct);
        var existing = await db.FindPostedMessageAsync(EntityType, e.MusterId, ct);

        // Target = the muster's own channel, falling back to the configured default.
        var target = muster is null ? 0UL
            : muster.ChannelId != 0 ? muster.ChannelId
            : (await musterSettings.GetAsync(e.GuildId, ct)).MusterChannelId;

        // Nothing to show (muster gone, or no channel resolved): drop any card we have.
        if (muster is null || target == 0)
        {
            await RemoveAsync(db, client, existing, logger, ct);
            return;
        }

        var currencyCode = muster.RewardAmount > 0
            ? await db.Currencies.Where(c => c.Id == muster.CurrencyId).Select(c => c.Code).FirstOrDefaultAsync(ct)
            : null;
        var embed = MusterEmbedRenderer.Render(muster, currencyCode, config["Web:BaseUrl"]);
        var components = MusterComponentBuilder.Build(muster);

        // Same channel → edit the existing card in place.
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
                // Message deleted out from under us — fall through and re-post (self-healing).
                logger.LogWarning(ex, "Muster card {MessageId} could not be edited; re-posting.", existing.MessageId);
            }
        }
        else if (existing is not null)
        {
            // Author moved the muster's channel — delete the old card before re-posting.
            await TryDeleteAsync(client, existing.ChannelId, existing.MessageId, logger, ct);
        }

        var sent = await client.Rest.SendMessageAsync(target, new MessageProperties { Embeds = [embed], Components = components }, cancellationToken: ct);

        if (existing is null)
        {
            db.PostedMessages.Add(new PostedMessage
            {
                Id = Guid.NewGuid(),
                GuildId = e.GuildId,
                EntityType = EntityType,
                EntityId = e.MusterId,
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
            logger.LogWarning(ex, "Muster card {MessageId} could not be deleted (already gone?).", messageId);
        }
    }
}

/// <summary>Logger category marker for <see cref="MusterBoardNotificationHandler"/> (which is static).</summary>
public sealed class MusterBoardNotification;

/// <summary>Handles a manual "remove card" request: delete the muster's Discord message and drop its board link.
/// The muster + roster + ledger stay in the DB (web keeps full history) — only the channel message goes.</summary>
public static class RemoveMusterCardHandler
{
    public static async Task Handle(
        RemoveMusterCard e, MusterDbContext db, GatewayClient client, ILogger<MusterBoardNotification> logger, CancellationToken ct)
    {
        var existing = await db.FindPostedMessageAsync(MusterBoardNotificationHandler.EntityType, e.MusterId, ct);
        if (existing is null || existing.GuildId != e.GuildId)
        {
            return; // unknown card, or a guild mismatch — never touch another guild's message
        }

        try
        {
            await client.Rest.DeleteMessageAsync(existing.ChannelId, existing.MessageId, cancellationToken: ct);
        }
        catch (RestException ex)
        {
            logger.LogWarning(ex, "Muster card {MessageId} could not be deleted (already gone?).", existing.MessageId);
        }

        await db.PostedMessages.Where(p => p.Id == existing.Id).ExecuteDeleteAsync(ct);
    }
}
