using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord.Gateway;
using NetCord.Rest;

namespace Muster.Bot.Questing.Handlers;

/// <summary>
/// Renders the live quest board into the configured Discord channel(s). Runs only in the Bot host (it owns the
/// gateway/REST client); quest lifecycle events reach it over the durable <c>quest-board</c> SQL queue, so a quest
/// changed from any host (web, API, bot, the auto-resolve sweep) updates the card.
///
/// A quest has <b>one</b> card that lives in the channel its current phase belongs to: mod-only states (pending
/// intake, disputed, awaiting final sign-off) go to the private staff channel, everything else to the public board.
/// As a quest crosses that boundary the card <b>moves</b> — Discord can't relocate a message, so the old one is
/// deleted and a fresh one posted (the <see cref="PostedMessage"/> row is repointed). Idempotent: each event
/// re-reads current state and edits the tracked message in place when the channel hasn't changed.
/// </summary>
public static class QuestBoardNotificationHandler
{
    public const string EntityType = "quest";

    /// <summary>Which channel a quest's card belongs in for a given state — 0 = don't post (scheduled, board off,
    /// or a mod-only state with no mod channel set, keeping admin-only needs off the public board).</summary>
    public static ulong TargetChannel(QuestStatus status, ulong publicChannel, ulong modChannel) => status switch
    {
        QuestStatus.Scheduled => 0, // not posted until it opens (activation re-publishes a Created moment)
        QuestStatus.PendingApproval or QuestStatus.Disputed or QuestStatus.PendingFinal => modChannel,
        _ => publicChannel,
    };

    private static bool IsTerminal(QuestStatus status) =>
        status is QuestStatus.Closed or QuestStatus.Cancelled or QuestStatus.Expired;

    public static async Task Handle(
        QuestLifecycleNotified e,
        MusterDbContext db,
        IQuestReadService reads,
        GatewayClient client,
        IConfiguration config,
        ILogger<QuestBoardNotification> logger,
        CancellationToken ct)
    {
        var settings = await db.GetQuestSettingsAsync(e.GuildId, ct);
        var quest = await reads.GetQuestDetailAsync(e.GuildId, e.QuestId, ct);
        var existing = await db.FindPostedMessageAsync(EntityType, e.QuestId, ct);

        var publicChannel = settings.QuestChannelId;
        var target = quest is null
            ? 0UL // quest gone — nothing to render anywhere
            : TargetChannel(quest.Status, publicChannel, settings.QuestModChannelId);

        // A terminal quest that was NEVER public (rejected at intake) keeps its card in the mod channel — don't
        // surface it publicly just because it closed. A quest that WAS public (so a dispute / final sign-off was
        // only a temporary detour into the mod channel) returns to the public board on resolution.
        if (quest is not null && existing is not null && target != 0 && IsTerminal(quest.Status) && !existing.EverPublic)
        {
            target = existing.ChannelId;
        }

        // Nowhere to show it (disabled / scheduled / mod-only with no mod channel / quest gone): drop any card we have.
        if (target == 0)
        {
            await RemoveAsync(db, client, existing, logger, ct);
            return;
        }

        var isPublic = publicChannel != 0 && target == publicChannel;
        // Notes can be personal → only the mod card shows them; the public board card omits them.
        var embed = QuestEmbedRenderer.Render(quest!, e.GuildId, config["Web:BaseUrl"], includeNotes: !isPublic);
        var components = QuestComponentBuilder.Build(quest!, e.GuildId, isMod: !isPublic);

        // Same channel → edit the existing card in place.
        if (existing is not null && existing.ChannelId == target)
        {
            try
            {
                await client.Rest.ModifyMessageAsync(target, existing.MessageId, m => { m.Embeds = [embed]; m.Components = components; }, cancellationToken: ct);
                existing.EverPublic |= isPublic;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (RestException ex)
            {
                // Message deleted out from under us — fall through and re-post (self-healing).
                logger.LogWarning(ex, "Quest board message {MessageId} could not be edited; re-posting.", existing.MessageId);
            }
        }
        else if (existing is not null)
        {
            // Crossing channels (e.g. intake accept moves mod → public, or a dispute resolves back to public):
            // delete the old card before re-posting.
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
                EntityId = e.QuestId,
                ChannelId = target,
                MessageId = sent.Id,
                EverPublic = isPublic,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.ChannelId = target;
            existing.MessageId = sent.Id;
            existing.EverPublic |= isPublic;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Delete a tracked card's Discord message (best-effort) and drop its row.</summary>
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
            logger.LogWarning(ex, "Quest board message {MessageId} could not be deleted (already gone?).", messageId);
        }
    }
}

/// <summary>Logger category marker for <see cref="QuestBoardNotificationHandler"/> (which is static).</summary>
public sealed class QuestBoardNotification;
