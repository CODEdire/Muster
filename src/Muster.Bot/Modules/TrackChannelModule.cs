using Microsoft.Extensions.DependencyInjection;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Commands.Tracking;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for per-channel background-tracking config (admin-only). Logic in <see cref="TrackedChannelCommandService"/>.</summary>
public class TrackChannelModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("track-voice", "Monitor a voice channel for always-on reward accrual.")]
    public Task VoiceAsync(
        [SlashCommandParameter(Name = "channel", Description = "Voice channel to monitor")] Channel channel,
        [SlashCommandParameter(Name = "points-per-minute", Description = "Points per eligible minute")] int pointsPerMinute = 1,
        [SlashCommandParameter(Name = "daily-cap", Description = "Max background points per member per day (0 = uncapped)")] int dailyCap = 0,
        [SlashCommandParameter(Name = "require-unmuted", Description = "Skip muted/deafened members (default true)")] bool requireUnmuted = true,
        [SlashCommandParameter(Name = "require-not-alone", Description = "Skip when alone in the channel (default true)")] bool requireNotAlone = true)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<TrackedChannelCommandService>()
                .SetVoiceAsync(guildId, channel.Id, pointsPerMinute, dailyCap, requireUnmuted, requireNotAlone),
            RequiredRole.Admin,
            auditAction: "track.voice");

    [SlashCommand("track-text", "Monitor a text channel for activity stats.")]
    public Task TextAsync(
        [SlashCommandParameter(Name = "channel", Description = "Text channel to monitor")] Channel channel)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<TrackedChannelCommandService>().SetTextAsync(guildId, channel.Id),
            RequiredRole.Admin,
            auditAction: "track.text");

    [SlashCommand("track-untrack", "Stop monitoring a channel.")]
    public Task UntrackAsync(
        [SlashCommandParameter(Name = "channel", Description = "Channel to stop monitoring")] Channel channel)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<TrackedChannelCommandService>().RemoveAsync(guildId, channel.Id),
            RequiredRole.Admin,
            auditAction: "track.untrack");

    [SlashCommand("track-channels", "List the channels being monitored.")]
    public Task ListAsync()
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<TrackedChannelCommandService>().ListAsync(guildId),
            RequiredRole.Admin);
}
