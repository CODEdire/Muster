using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for tracking-session commands (admin-only). Logic in <see cref="TrackingCommandService"/>.</summary>
public class TrackModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("track-start", "Open a voice tracking session in a channel.")]
    public Task<Reply> StartAsync(
        [SlashCommandParameter(Name = "channel", Description = "Voice channel to track")] Channel channel)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<TrackingCommandService>().StartAsync(guildId, Context.User.Id, channel.Id),
            RequiredRole.Admin,
            auditAction: "track.start");

    [SlashCommand("track-stop", "Close a tracking session and award attendance.")]
    public Task<Reply> StopAsync(
        [SlashCommandParameter(Name = "session", Description = "Session id from /track-start")] string session)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<TrackingCommandService>().StopAsync(guildId, session),
            RequiredRole.Admin,
            auditAction: "track.stop");
}
