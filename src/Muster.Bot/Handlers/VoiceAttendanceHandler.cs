using Microsoft.Extensions.DependencyInjection;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot.Handlers;

/// <summary>
/// Feeds Discord voice-state changes into both tracking planes: bounded-session attendance, and the
/// always-on background plane (reconciled against the live voice roster from the gateway cache).
/// </summary>
public class VoiceAttendanceHandler(IServiceScopeFactory scopeFactory, GatewayClient client) : IVoiceStateUpdateGatewayHandler
{
    public async ValueTask HandleAsync(VoiceState arg)
    {
        using var scope = scopeFactory.CreateScope();

        if (arg.User is { IsBot: false } user)
        {
            var members = scope.ServiceProvider.GetRequiredService<MemberSyncService>();
            await members.UpsertAsync(arg.GuildId, arg.UserId, user.Username, user.GlobalName, user.AvatarHash, user.Nickname, user.RoleIds);
        }

        var roster = VoiceRoster.Snapshot(client, arg.GuildId);

        var sessions = scope.ServiceProvider.GetRequiredService<TrackingSessionService>();
        await sessions.ReconcileSessionsAsync(arg.GuildId, roster);

        var background = scope.ServiceProvider.GetRequiredService<BackgroundTrackingService>();
        await background.ReconcileGuildAsync(arg.GuildId, roster);
    }
}
