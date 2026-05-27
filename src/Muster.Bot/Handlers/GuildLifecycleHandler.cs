using Microsoft.Extensions.DependencyInjection;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Bot.Handlers;

/// <summary>
/// Maintains the Guild table across the bot's membership: provisions on join (capturing the owner and
/// role snapshot), updates on rename, and marks inactive when the bot is removed.
/// </summary>
public class GuildLifecycleHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<GuildLifecycleHandler> logger)
    : IGuildCreateGatewayHandler, IGuildUpdateGatewayHandler, IGuildDeleteGatewayHandler
{
    public ValueTask HandleAsync(GuildCreateEventArgs arg)
    {
        // On reconnect/startup the payload may describe an unavailable guild with no data.
        if (arg.Guild is not { } guild)
        {
            return ValueTask.CompletedTask;
        }

        return RunInScopeAsync(async sp =>
        {
            await sp.GetRequiredService<GuildProvisioningService>()
                .EnsureGuildAsync(guild.Id, guild.Name, guild.IconHash, guild.OwnerId);

            await sp.GetRequiredService<RoleSyncService>().SyncAllAsync(
                guild.Id, guild.Roles.Values.Select(r => (r.Id, r.Name, (ulong)r.Permissions)));

            // Snapshot the voice/text channel roster so the web pickers work without a live Discord call.
            var channels = guild.Channels.Values
                .Select(c => (Channel: c, Kind: ChannelLifecycleHandler.MapKind(c)))
                .Where(x => x.Kind is not null)
                .Select(x => (x.Channel.Id, x.Channel.Name, x.Kind!.Value, x.Channel.Position))
                .ToList();
            await sp.GetRequiredService<ChannelSyncService>().SyncAllAsync(guild.Id, channels);

            // Backfill the roster the GuildCreate payload already carries (needs the GuildUsers intent).
            // For guilds above Discord's large threshold this may be partial — /syncmembers pulls the rest.
            // Bots are included (with IsBot) so they can serve as API service actors; human lists filter them.
            var snapshots = guild.Users.Values
                .Select(u => new MemberSnapshot(u.Id, u.Username, u.GlobalName, u.AvatarHash, u.Nickname, u.RoleIds, u.IsBot))
                .ToList();
            var synced = await sp.GetRequiredService<MemberSyncService>().SyncGuildAsync(guild.Id, snapshots);

            logger.LogInformation("Provisioned guild {GuildId} ({GuildName}); synced {Count} members", guild.Id, guild.Name, synced);
        });
    }

    // Guild renamed / icon changed / ownership transferred.
    public ValueTask HandleAsync(NetCord.Gateway.Guild guild)
        => RunInScopeAsync(sp => sp.GetRequiredService<GuildProvisioningService>()
            .EnsureGuildAsync(guild.Id, guild.Name, guild.IconHash, guild.OwnerId));

    // Bot removed from the guild (ignore transient unavailability during outages).
    public ValueTask HandleAsync(GuildDeleteEventArgs arg)
        => arg.IsUnavailable
            ? ValueTask.CompletedTask
            : RunInScopeAsync(sp => sp.GetRequiredService<GuildProvisioningService>().MarkInactiveAsync(arg.GuildId));

    /// <summary>
    /// Run work in a fresh DI scope, tolerating a gateway event that arrives while the host is tearing
    /// down (dev/Aspire restart, reconnect, or shutdown). These handlers are singletons holding the root
    /// scope factory, so an <see cref="ObjectDisposedException"/> here means the root provider is being
    /// disposed — there's nothing useful to do, and the next connect re-delivers the event.
    /// </summary>
    private async ValueTask RunInScopeAsync(Func<IServiceProvider, Task> work)
    {
        IServiceScope scope;
        try
        {
            scope = scopeFactory.CreateScope();
        }
        catch (ObjectDisposedException)
        {
            logger.LogDebug("Skipping guild lifecycle event — the host is shutting down.");
            return;
        }

        using (scope)
        {
            try
            {
                await work(scope.ServiceProvider);
            }
            catch (ObjectDisposedException)
            {
                logger.LogDebug("Aborted guild lifecycle event — the host shut down mid-handling.");
            }
        }
    }
}
