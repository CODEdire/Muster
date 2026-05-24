using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace Muster.Bot.Handlers;

/// <summary>Routes reactions on tracked muster messages into check-in/reward recording.</summary>
public class MusterReactionHandler(IServiceScopeFactory scopeFactory) : IMessageReactionAddGatewayHandler
{
    public async ValueTask HandleAsync(MessageReactionAddEventArgs arg)
    {
        // Ignore DMs and the bot's own seeding reactions are handled by the service (unknown messages no-op).
        if (arg.GuildId is null)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var musters = scope.ServiceProvider.GetRequiredService<MusterService>();
        await musters.RecordReactionAsync(arg.MessageId, arg.UserId, arg.Emoji.Name ?? string.Empty);
    }
}
