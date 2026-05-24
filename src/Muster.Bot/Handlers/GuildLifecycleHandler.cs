using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace Muster.Bot.Handlers;

/// <summary>
/// Onboards guilds as they become available on the gateway. Resolved as a singleton, so it opens a
/// scope per event to use the scoped <see cref="GuildProvisioningService"/> / DbContext.
/// </summary>
public class GuildLifecycleHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<GuildLifecycleHandler> logger) : IGuildCreateGatewayHandler
{
    public async ValueTask HandleAsync(GuildCreateEventArgs arg)
    {
        // On reconnect/startup the payload may describe an unavailable guild with no data.
        if (arg.Guild is not { } guild)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<GuildProvisioningService>();
        await provisioning.EnsureGuildAsync(guild.Id, guild.Name, guild.IconHash);

        logger.LogInformation("Provisioned guild {GuildId} ({GuildName})", guild.Id, guild.Name);
    }
}
