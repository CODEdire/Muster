using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace Muster.Bot.Handlers;

/// <summary>Keeps the local role snapshot in sync so the Discord-permission admin bypass stays accurate.</summary>
public class RoleLifecycleHandler(IServiceScopeFactory scopeFactory)
    : IRoleCreateGatewayHandler, IRoleUpdateGatewayHandler, IRoleDeleteGatewayHandler
{
    public async ValueTask HandleAsync(Role role)
    {
        using var scope = scopeFactory.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleSyncService>();
        await roles.UpsertAsync(role.GuildId, role.Id, role.Name, (ulong)role.Permissions);
    }

    public async ValueTask HandleAsync(RoleDeleteEventArgs arg)
    {
        using var scope = scopeFactory.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleSyncService>();
        await roles.RemoveAsync(arg.GuildId, arg.RoleId);
    }
}
