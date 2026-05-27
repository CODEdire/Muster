using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Commands.Tracking;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for tracking-session commands (admin-only). Logic in <see cref="TrackingCommandService"/>.</summary>
public class TrackModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("track-start", "Open a named voice tracking session in a channel.")]
    public Task StartAsync(
        [SlashCommandParameter(Name = "channel", Description = "Voice channel to track")] Channel channel,
        [SlashCommandParameter(Name = "name", Description = "A name for this session (e.g. 'Friday raid')")] string name,
        [SlashCommandParameter(Name = "skip-muted", Description = "Pause reward time while a member is muted/deafened (default true)")] bool skipMuted = true,
        [SlashCommandParameter(Name = "skip-alone", Description = "Pause reward time while a member is alone in the channel (default false)")] bool skipAlone = false)
        => RunAsync(
            async (sp, guildId) =>
            {
                var result = await sp.GetRequiredService<TrackingCommandService>()
                    .StartAsync(guildId, Context.User.Id, channel.Id, name, requireUnmuted: skipMuted, requireNotAlone: skipAlone);

                // Members already in the channel produce no voice event, so scan the current roster now
                // (otherwise they wouldn't be counted until the next periodic sweep).
                await sp.GetRequiredService<GuildReconcileCoordinator>().ReconcileNowAsync(guildId);

                return result;
            },
            RequiredRole.Admin,
            auditAction: "track.start");

    [SlashCommand("track-stop", "Close an active tracking session and award attendance.")]
    public Task StopAsync(
        [SlashCommandParameter(Name = "session", Description = "Pick an active session",
            AutocompleteProviderType = typeof(ActiveSessionAutocompleteProvider))] string session)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<TrackingCommandService>().StopAsync(guildId, session),
            RequiredRole.Admin,
            auditAction: "track.stop");
}
