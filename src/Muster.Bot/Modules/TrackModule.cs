using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Commands.Tracking;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot.Modules;

/// <summary>
/// Root <c>/track</c> command. Member-facing leaves (<c>privacy</c>, <c>leaderboard</c>) are open to all;
/// the <c>session</c> and <c>background</c> groups are admin-only. Logic lives in the tracking command/read
/// services — these are thin adapters.
/// </summary>
[SlashCommand("track", "Participation tracking: sessions, background channels, your privacy, and the leaderboard.")]
public class TrackModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SubSlashCommand("privacy", "Choose how this server tracks your participation.")]
    public Task PrivacyAsync(
        [SlashCommandParameter(Name = "choice", Description = "Default = follow server; In = opt in; BackgroundOut = no passive tracking; AllOut = none")]
        TrackingChoice choice)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<TrackingPreferenceCommandService>().SetAsync(guildId, Context.User.Id, choice),
            RequiredRole.None);

    [SubSlashCommand("leaderboard", "Show the top members by voice participation time.")]
    public Task LeaderboardAsync()
        => RunAsync(async (sp, guildId) =>
        {
            var board = await sp.GetRequiredService<ParticipationReadService>().VoiceLeaderboardAsync(guildId, 10);
            if (board.Count == 0)
            {
                return CommandResult.Ok("No voice activity tracked yet.");
            }

            var lines = board.Select((e, i) => $"{i + 1}. <@{e.UserId}> — **{FormatDuration(e.VoiceMinutes)}**");
            return CommandResult.Ok("**Voice participation**\n" + string.Join("\n", lines));
        });

    internal static string FormatDuration(int minutes)
    {
        var hours = minutes / 60;
        var mins = minutes % 60;
        return hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
    }

    /// <summary>Manual tracking sessions (admin).</summary>
    [SubSlashCommand("session", "Open and close manual tracking sessions.")]
    public class SessionModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
    {
        [SubSlashCommand("start", "Open a named voice tracking session in a channel.")]
        public Task StartAsync(
            [SlashCommandParameter(Name = "channel", Description = "Voice channel to track")] Channel channel,
            [SlashCommandParameter(Name = "name", Description = "A name for this session (e.g. 'Friday raid')")] string name,
            [SlashCommandParameter(Name = "skip-muted", Description = "Pause while muted, i.e. can't speak (default false — a muted member may still be present)")] bool skipMuted = false,
            [SlashCommandParameter(Name = "skip-deafened", Description = "Pause while deafened, i.e. checked out (default true)")] bool skipDeafened = true,
            [SlashCommandParameter(Name = "skip-alone", Description = "Pause while alone in the channel (default false)")] bool skipAlone = false)
            => RunAsync(
                async (sp, guildId) =>
                {
                    var result = await sp.GetRequiredService<TrackingCommandService>()
                        .StartAsync(guildId, Context.User.Id, channel.Id, name, (channel as IGuildChannel)?.Name,
                            requireUnmuted: skipMuted, requireUndeafened: skipDeafened, requireNotAlone: skipAlone);

                    // Members already in the channel produce no voice event — scan the current roster now.
                    await sp.GetRequiredService<GuildReconcileCoordinator>().ReconcileNowAsync(guildId);
                    return result;
                },
                RequiredRole.Admin,
                auditAction: "track.session.start");

        [SubSlashCommand("stop", "Close an active tracking session and award attendance.")]
        public Task StopAsync(
            [SlashCommandParameter(Name = "session", Description = "Pick an active session",
                AutocompleteProviderType = typeof(ActiveSessionAutocompleteProvider))] string session)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<TrackingCommandService>().StopAsync(guildId, session),
                RequiredRole.Admin,
                auditAction: "track.session.stop");
    }

    /// <summary>Background (always-on) channel monitoring config (admin).</summary>
    [SubSlashCommand("background", "Configure which channels are monitored in the background.")]
    public class BackgroundModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
    {
        [SubSlashCommand("voice", "Monitor a voice channel for always-on reward accrual.")]
        public Task VoiceAsync(
            [SlashCommandParameter(Name = "channel", Description = "Voice channel to monitor")] Channel channel,
            [SlashCommandParameter(Name = "points-per-minute", Description = "Points per eligible minute")] int pointsPerMinute = 1,
            [SlashCommandParameter(Name = "daily-cap", Description = "Max background points per member per day (0 = uncapped)")] int dailyCap = 0,
            [SlashCommandParameter(Name = "require-unmuted", Description = "Skip muted members, i.e. can't speak (default false)")] bool requireUnmuted = false,
            [SlashCommandParameter(Name = "require-undeafened", Description = "Skip deafened members, i.e. checked out (default true)")] bool requireUndeafened = true,
            [SlashCommandParameter(Name = "require-not-alone", Description = "Skip when alone in the channel (default true)")] bool requireNotAlone = true)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<TrackedChannelCommandService>()
                    .SetVoiceAsync(guildId, channel.Id, pointsPerMinute, dailyCap, requireUnmuted, requireUndeafened, requireNotAlone, channelName: (channel as IGuildChannel)?.Name),
                RequiredRole.Admin,
                auditAction: "track.background.voice");

        [SubSlashCommand("text", "Monitor a text channel for activity (optionally rewarding messages).")]
        public Task TextAsync(
            [SlashCommandParameter(Name = "channel", Description = "Text channel to monitor")] Channel channel,
            [SlashCommandParameter(Name = "points-per-message", Description = "Points per reward event (0 = stats only)")] int pointsPerMessage = 0,
            [SlashCommandParameter(Name = "messages-per-point", Description = "Messages required per reward event (default 1)")] int messagesPerPoint = 1,
            [SlashCommandParameter(Name = "cooldown-seconds", Description = "Min seconds between rewarded messages (0 = none)")] int cooldownSeconds = 0,
            [SlashCommandParameter(Name = "daily-cap", Description = "Max message points per member per day (0 = uncapped)")] int dailyCap = 0)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<TrackedChannelCommandService>()
                    .SetTextAsync(guildId, channel.Id, pointsPerMessage, messagesPerPoint, cooldownSeconds, dailyCap, channelName: (channel as IGuildChannel)?.Name),
                RequiredRole.Admin,
                auditAction: "track.background.text");

        [SubSlashCommand("remove", "Stop monitoring a channel.")]
        public Task RemoveAsync(
            [SlashCommandParameter(Name = "channel", Description = "Channel to stop monitoring")] Channel channel)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<TrackedChannelCommandService>().RemoveAsync(guildId, channel.Id),
                RequiredRole.Admin,
                auditAction: "track.background.remove");

        [SubSlashCommand("list", "List the channels being monitored.")]
        public Task ListAsync()
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<TrackedChannelCommandService>().ListAsync(guildId),
                RequiredRole.Admin);
    }
}
