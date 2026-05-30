using Microsoft.Extensions.Hosting;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;

namespace Muster.Bot.Membership;

/// <summary>
/// Per-feature composition for the Membership slice. <see cref="AddMembershipFeature"/> registers the
/// gateway handlers that keep local guild/member/role state in sync with Discord;
/// <see cref="UseMembershipModule"/> registers the slash command modules on the built host.
/// </summary>
public static class MembershipExtensions
{
    public static IHostApplicationBuilder AddMembershipFeature(this IHostApplicationBuilder builder)
    {
        // Discord gateway lifecycle events — guild add/remove, member join/leave/update, role create/update/delete.
        builder.Services.AddGatewayHandler<GuildLifecycleHandler>();
        builder.Services.AddGatewayHandler<MemberLifecycleHandler>();
        builder.Services.AddGatewayHandler<RoleLifecycleHandler>();

        return builder;
    }

    public static IHost UseMembershipModule(this IHost host)
    {
        host.AddApplicationCommandModule<MembersModule>();
        host.AddApplicationCommandModule<ProfileModule>();
        return host;
    }
}
