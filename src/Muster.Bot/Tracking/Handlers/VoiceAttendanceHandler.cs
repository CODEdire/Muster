using Microsoft.Extensions.DependencyInjection;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Bot.Tracking.Handlers;

/// <summary>
/// Feeds Discord voice-state changes into tracking. Member sync runs immediately; the reconcile itself is
/// handed to <see cref="GuildReconcileCoordinator"/>, which debounces bursts and serializes per guild.
/// </summary>
public class VoiceAttendanceHandler(IServiceScopeFactory scopeFactory, GuildReconcileCoordinator coordinator)
    : IVoiceStateUpdateGatewayHandler
{
    public async ValueTask HandleAsync(VoiceState arg)
    {
        if (arg.User is { IsBot: false } user)
        {
            using var scope = scopeFactory.CreateScope();
            var members = scope.ServiceProvider.GetRequiredService<MemberSyncService>();
            await members.UpsertAsync(arg.GuildId, arg.UserId, user.Username, user.GlobalName, user.AvatarHash, user.Nickname, user.RoleIds);
        }

        coordinator.Schedule(arg.GuildId);
    }
}
