using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace Muster.Bot.Handlers;

/// <summary>Records stats-only message activity (not rewardable in v1). Ignores bots and DMs.</summary>
public class MessageActivityHandler(IServiceScopeFactory scopeFactory) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message arg)
    {
        if (arg.GuildId is not { } guildId || arg.Author.IsBot)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();

        var members = scope.ServiceProvider.GetRequiredService<MemberSyncService>();
        await members.UpsertAsync(guildId, arg.Author.Id, arg.Author.Username, arg.Author.GlobalName, arg.Author.AvatarHash);

        var activity = scope.ServiceProvider.GetRequiredService<ActivityService>();
        await activity.RecordMessageAsync(guildId, arg.ChannelId, arg.Author.Id, arg.Id, arg.CreatedAt);
    }
}
