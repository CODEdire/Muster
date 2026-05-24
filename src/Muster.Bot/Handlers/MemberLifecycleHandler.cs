using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace Muster.Bot.Handlers;

/// <summary>
/// Keeps the local member tables in sync with Discord: a member joining or having their nickname/roles
/// change upserts the row; leaving removes the guild membership. Requires the privileged Server Members
/// (GuildUsers) intent.
/// </summary>
public class MemberLifecycleHandler(IServiceScopeFactory scopeFactory)
    : IGuildUserAddGatewayHandler, IGuildUserUpdateGatewayHandler, IGuildUserRemoveGatewayHandler
{
    public ValueTask HandleAsync(GuildUser user) => UpsertAsync(user);

    public async ValueTask HandleAsync(GuildUserRemoveEventArgs arg)
    {
        using var scope = scopeFactory.CreateScope();
        var members = scope.ServiceProvider.GetRequiredService<MemberSyncService>();
        await members.RemoveMemberAsync(arg.GuildId, arg.User.Id);
    }

    private async ValueTask UpsertAsync(GuildUser user)
    {
        if (user.IsBot)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var members = scope.ServiceProvider.GetRequiredService<MemberSyncService>();
        await members.UpsertAsync(
            user.GuildId, user.Id, user.Username, user.GlobalName, user.AvatarHash, user.Nickname, user.RoleIds);
    }
}
