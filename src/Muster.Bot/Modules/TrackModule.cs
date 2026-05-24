using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for tracking-session commands. Logic lives in <see cref="TrackingCommandService"/>.</summary>
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
        var commands = scope.ServiceProvider.GetRequiredService<TrackingCommandService>();
        var result = await commands.StartAsync(guild.Id, Context.User.Id, channel.Id);
        return result.Message;
    }

    [SlashCommand("track-stop", "Close a tracking session and award attendance.")]
    public async Task<string> StopAsync(
        [SlashCommandParameter(Name = "session", Description = "Session id from /track-start")] string session)
    {
        if (Context.Guild is not { } guild)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<TrackingCommandService>();
        var result = await commands.StopAsync(guild.Id, session);
        return result.Message;
    }
}
