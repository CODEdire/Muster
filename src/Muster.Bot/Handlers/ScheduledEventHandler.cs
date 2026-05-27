using Microsoft.Extensions.DependencyInjection;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot.Handlers;

/// <summary>
/// Binds tracking sessions to Discord scheduled events: open one when an event goes active, close it
/// when the event completes or is cancelled.
/// </summary>
public class ScheduledEventHandler(IServiceScopeFactory scopeFactory, GatewayClient client)
    : IGuildScheduledEventUpdateGatewayHandler, IGuildScheduledEventCreateGatewayHandler
{
    public ValueTask HandleAsync(GuildScheduledEvent arg) => ReconcileAsync(arg);

    private async ValueTask ReconcileAsync(GuildScheduledEvent scheduledEvent)
    {
        using var scope = scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<TrackingSessionService>();

        switch (scheduledEvent.Status)
        {
            case GuildScheduledEventStatus.Active when scheduledEvent.ChannelId is { } channelId:
                await sessions.EnsureForScheduledEventAsync(scheduledEvent.GuildId, channelId, scheduledEvent.Id, scheduledEvent.Name);
                // Members already in the channel produce no voice event — scan the current roster so they're counted now.
                await sessions.ReconcileSessionsAsync(scheduledEvent.GuildId, VoiceRoster.Snapshot(client, scheduledEvent.GuildId));
                break;

            case GuildScheduledEventStatus.Completed:
            case GuildScheduledEventStatus.Canceled:
                await sessions.CloseForScheduledEventAsync(scheduledEvent.GuildId, scheduledEvent.Id);
                break;
        }
    }
}
