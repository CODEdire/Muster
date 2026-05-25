using Microsoft.Extensions.DependencyInjection;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot.Handlers;

/// <summary>Feeds Discord voice-state changes into tracking-session attendance accumulation.</summary>
public class VoiceAttendanceHandler(IServiceScopeFactory scopeFactory) : IVoiceStateUpdateGatewayHandler
{
    public async ValueTask HandleAsync(VoiceState arg)
    {
        using var scope = scopeFactory.CreateScope();

        if (arg.User is { IsBot: false } user)
        {
            var members = scope.ServiceProvider.GetRequiredService<MemberSyncService>();
            await members.UpsertAsync(arg.GuildId, arg.UserId, user.Username, user.GlobalName, user.AvatarHash, user.Nickname, user.RoleIds);
        }

        var sessions = scope.ServiceProvider.GetRequiredService<TrackingSessionService>();
        await sessions.ProcessVoiceStateAsync(arg.GuildId, arg.UserId, arg.ChannelId);
    }
}
