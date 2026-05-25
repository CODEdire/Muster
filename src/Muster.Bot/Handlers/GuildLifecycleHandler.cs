using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

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
    public async ValueTask HandleAsync(GuildCreateEventArgs arg)
    {
        // On reconnect/startup the payload may describe an unavailable guild with no data.
        if (arg.Guild is not { } guild)
        {
            return;
        }

        using var scope = TryCreateScope();
        if (scope is null)
        {
            return;
        }

        var provisioning = scope.ServiceProvider.GetRequiredService<GuildProvisioningService>();
        await provisioning.EnsureGuildAsync(guild.Id, guild.Name, guild.IconHash, guild.OwnerId);

        var roleSync = scope.ServiceProvider.GetRequiredService<RoleSyncService>();
        await roleSync.SyncAllAsync(
            guild.Id, guild.Roles.Values.Select(r => (r.Id, r.Name, (ulong)r.Permissions)));

        logger.LogInformation("Provisioned guild {GuildId} ({GuildName})", guild.Id, guild.Name);
    }

    // Guild renamed / icon changed / ownership transferred.
    public async ValueTask HandleAsync(Guild guild)
    {
        using var scope = TryCreateScope();
        if (scope is null)
        {
            return;
        }

        var provisioning = scope.ServiceProvider.GetRequiredService<GuildProvisioningService>();
        await provisioning.EnsureGuildAsync(guild.Id, guild.Name, guild.IconHash, guild.OwnerId);
    }

    // Bot removed from the guild (ignore transient unavailability during outages).
    public async ValueTask HandleAsync(GuildDeleteEventArgs arg)
    {
        if (arg.IsUnavailable)
        {
            return;
        }

        using var scope = TryCreateScope();
        if (scope is null)
        {
            return;
        }

        var provisioning = scope.ServiceProvider.GetRequiredService<GuildProvisioningService>();
        await provisioning.MarkInactiveAsync(arg.GuildId);
    }

    /// <summary>
    /// Create a DI scope, tolerating a late gateway event delivered after the host's root provider was
    /// disposed during shutdown/reconnect teardown (this handler is a singleton holding the root factory).
    /// </summary>
    private IServiceScope? TryCreateScope()
    {
        try
        {
            return scopeFactory.CreateScope();
        }
        catch (ObjectDisposedException)
        {
            logger.LogDebug("Skipping guild lifecycle event — the host is shutting down.");
            return null;
        }
    }
}
