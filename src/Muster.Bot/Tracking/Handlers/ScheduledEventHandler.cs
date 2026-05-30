using Microsoft.Extensions.DependencyInjection;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot.Tracking.Handlers;

/// <summary>
/// Binds tracking sessions to Discord scheduled events: open one when an event goes active, close it
/// when the event completes or is cancelled. Each transition lands a system-actor audit row so admins can
/// trace "who opened this session?" even when the trigger was a Discord event, not a slash command.
/// </summary>
public class ScheduledEventHandler(IServiceScopeFactory scopeFactory, GatewayClient client, GuildReconcileCoordinator coordinator)
    : IGuildScheduledEventUpdateGatewayHandler, IGuildScheduledEventCreateGatewayHandler
{
    public ValueTask HandleAsync(GuildScheduledEvent arg) => ReconcileAsync(arg);

    private async ValueTask ReconcileAsync(GuildScheduledEvent scheduledEvent)
    {
        using var scope = scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<TrackingSessionService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();

        switch (scheduledEvent.Status)
        {
            case GuildScheduledEventStatus.Active when scheduledEvent.ChannelId is { } channelId:
                await sessions.EnsureForScheduledEventAsync(
                    scheduledEvent.GuildId, channelId, scheduledEvent.Id, scheduledEvent.Name, ChannelName(scheduledEvent.GuildId, channelId));
                await audit.RecordAsync(
                    scheduledEvent.GuildId, actorUserId: 0, AuditActions.Track.ScheduledEventSessionOpen,
                    new ScheduledEventAuditPayload(scheduledEvent.Id, scheduledEvent.Name, channelId),
                    origin: AuditOrigin.System);
                // Members already in the channel produce no voice event — scan the current roster so they're counted now.
                await coordinator.ReconcileNowAsync(scheduledEvent.GuildId);
                break;

            case GuildScheduledEventStatus.Completed:
            case GuildScheduledEventStatus.Canceled:
                await sessions.CloseForScheduledEventAsync(scheduledEvent.GuildId, scheduledEvent.Id);
                await audit.RecordAsync(
                    scheduledEvent.GuildId, actorUserId: 0, AuditActions.Track.ScheduledEventSessionClose,
                    new ScheduledEventAuditPayload(scheduledEvent.Id, scheduledEvent.Name, scheduledEvent.ChannelId ?? 0),
                    origin: AuditOrigin.System);
                break;
        }
    }

    /// <summary>Payload for a system-actor audit row recording a Discord scheduled event triggering session open/close.</summary>
    private record ScheduledEventAuditPayload(ulong EventId, string EventName, ulong ChannelId);

    private string? ChannelName(ulong guildId, ulong channelId)
        => client.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Channels.TryGetValue(channelId, out var channel)
            ? channel.Name
            : null;
}
