using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

public class TrackModule(IServiceScopeFactory scopeFactory) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("track-start", "Open a voice tracking session in a channel.")]
    public async Task<string> StartAsync(
        [SlashCommandParameter(Name = "channel", Description = "Voice channel to track")] Channel channel)
    {
        if (Context.Guild is not { } guild)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<TrackingSessionService>();
        var session = await sessions.OpenManualAsync(guild.Id, channel.Id, Context.User.Id);

        return $"Started tracking session `{session.Id}` in <#{channel.Id}>. Close it with `/track-stop`.";
    }

    [SlashCommand("track-stop", "Close a tracking session and award attendance.")]
    public async Task<string> StopAsync(
        [SlashCommandParameter(Name = "session", Description = "Session id from /track-start")] string session)
    {
        if (Context.Guild is null)
        {
            return "This command can only be used in a server.";
        }

        if (!Guid.TryParse(session, out var sessionId))
        {
            return "That doesn't look like a valid session id.";
        }

        using var scope = scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<TrackingSessionService>();
        await sessions.CloseAsync(sessionId);

        return $"Closed session `{sessionId}` and awarded voice attendance.";
    }
}
