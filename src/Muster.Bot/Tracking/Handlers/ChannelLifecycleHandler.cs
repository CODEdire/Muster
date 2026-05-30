using Microsoft.Extensions.DependencyInjection;
using NetCord;
using NetCord.Hosting.Gateway;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Bot.Tracking.Handlers;

/// <summary>Keeps the local channel snapshot in sync so the web UI's channel pickers stay current.</summary>
public class ChannelLifecycleHandler(IServiceScopeFactory scopeFactory)
    : IGuildChannelCreateGatewayHandler, IGuildChannelUpdateGatewayHandler, IGuildChannelDeleteGatewayHandler
{
    // Create and Update share the upsert path; Delete removes.
    async ValueTask IGuildChannelCreateGatewayHandler.HandleAsync(IGuildChannel channel) => await UpsertAsync(channel);
    async ValueTask IGuildChannelUpdateGatewayHandler.HandleAsync(IGuildChannel channel) => await UpsertAsync(channel);

    public async ValueTask HandleAsync(IGuildChannel channel)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ChannelSyncService>()
            .RemoveAsync(channel.GuildId, channel.Id);
    }

    private async ValueTask UpsertAsync(IGuildChannel channel)
    {
        if (MapKind(channel) is not { } kind)
        {
            return; // not a standalone voice/text channel — ignore
        }

        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ChannelSyncService>()
            .UpsertAsync(channel.GuildId, channel.Id, channel.Name, kind, channel.Position);
    }

    /// <summary>
    /// Map a NetCord guild channel to our stored kind, or null to skip it. Voice and stage channels are
    /// Voice; standalone text/announcement channels are Text. Threads (which derive from
    /// <c>TextGuildChannel</c>), categories, and forums are skipped.
    /// </summary>
    public static GuildChannelKind? MapKind(IGuildChannel channel) => channel switch
    {
        IVoiceGuildChannel => GuildChannelKind.Voice,
        GuildThread => null,
        TextGuildChannel => GuildChannelKind.Text,
        _ => null,
    };
}
