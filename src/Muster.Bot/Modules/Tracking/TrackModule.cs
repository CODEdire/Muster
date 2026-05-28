using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Commands.Tracking;
using Muster.Infrastructure.Commands.Membership;
using Muster.Infrastructure.Services.Tracking;
using Muster.Infrastructure.Services.Platform;
using Muster.Persistence;
using Muster.Persistence.Queries;

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
            var body = "**Voice participation**\n" + string.Join("\n", lines);

            var url = sp.GetRequiredService<Muster.Infrastructure.Platform.WebLinkBuilder>().Sessions(guildId);
            if (url is not null)
            {
                body += $"\n\n🌐 Full sessions view: {url}";
            }

            return CommandResult.Ok(body);
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

        [SubSlashCommand("new", "Get a link to the web wizard for starting a session (channel pickers + options).")]
        public Task NewAsync()
            => RunAsync(
                (sp, guildId) =>
                {
                    var url = sp.GetRequiredService<Muster.Infrastructure.Platform.WebLinkBuilder>().NewSession(guildId);
                    return Task.FromResult(url is null
                        ? CommandResult.Error("No web URL is configured for this server — use `/track session start` instead.")
                        : CommandResult.Ok($"🌐 Start a session in the web wizard: {url}"));
                },
                RequiredRole.Admin);

        [SubSlashCommand("optout", "Opt yourself out of an active session (this session only — your standing preference is unchanged).")]
        public Task OptOutAsync(
            [SlashCommandParameter(Name = "session", Description = "Pick an active session",
                AutocompleteProviderType = typeof(ActiveSessionAutocompleteProvider))] string session)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<TrackingCommandService>()
                    .OptOutAsync(guildId, session, Context.User.Id, Context.User.Id, actorIsStaff: false),
                RequiredRole.None,
                auditAction: "track.session.optout");

        [SubSlashCommand("list", "List the active tracking sessions in this server.")]
        public Task ListAsync()
            => RunAsync(async (sp, guildId) =>
            {
                var actives = await sp.GetRequiredService<ParticipationReadService>().ActiveSessionsAsync(guildId);
                if (actives.Count == 0)
                {
                    return CommandResult.Ok("No active sessions right now.");
                }

                var lines = actives.Select(s =>
                    $"• **{s.Name}** — <#{s.VoiceChannelId}> · {s.Attendees} attendee{(s.Attendees == 1 ? "" : "s")}, {s.PresentNow} earning · started <t:{s.StartedAt.ToUnixTimeSeconds()}:R>");
                var body = "**Active sessions**\n" + string.Join("\n", lines);

                var url = sp.GetRequiredService<Muster.Infrastructure.Platform.WebLinkBuilder>().Sessions(guildId);
                if (url is not null) { body += $"\n\n🌐 {url}"; }
                return CommandResult.Ok(body);
            });

        [SubSlashCommand("info", "Show a session's status, roster size, and live counts.")]
        public Task InfoAsync(
            [SlashCommandParameter(Name = "session", Description = "Pick an active session",
                AutocompleteProviderType = typeof(ActiveSessionAutocompleteProvider))] string session)
            => RunAsync(async (sp, guildId) =>
            {
                if (!Guid.TryParse(session, out var id))
                {
                    return CommandResult.Error("That doesn't look like a valid session id.");
                }

                var d = await sp.GetRequiredService<ParticipationReadService>().SessionDetailAsync(guildId, id, includeEvents: false);
                if (d is null)
                {
                    return CommandResult.Error("No session with that id was found.");
                }

                var inVoice = d.Members.Count(m => m.InChannel);
                var earning = d.Members.Count(m => m.PresentNow);
                var totalMinutes = d.Members.Sum(m => m.TotalMinutes);
                var when = d.Active
                    ? $"started <t:{d.StartedAt.ToUnixTimeSeconds()}:R>"
                    : $"ran <t:{d.StartedAt.ToUnixTimeSeconds()}:f> – <t:{(d.EndedAt ?? d.StartedAt).ToUnixTimeSeconds()}:t>";

                var body =
                    $"**{d.Name}** — {(d.Active ? "🟢 Live" : "⚪ Completed")}\n" +
                    $"Channel: <#{d.VoiceChannelId}> · {when}\n" +
                    $"Attendees: **{d.Members.Count}**" + (d.Active ? $" · In voice: **{inVoice}** · Earning: **{earning}**" : "") + "\n" +
                    $"Voice minutes: **{totalMinutes}** · Rate: {d.PointsPerMinute} pts/min" +
                    (d.OpenedBy != 0 ? $"\nOpened by <@{d.OpenedBy}>" : "");

                var url = sp.GetRequiredService<Muster.Infrastructure.Platform.WebLinkBuilder>().Sessions(guildId);
                if (url is not null) { body += $"\n\n🌐 {url}/{d.Id}"; }
                return CommandResult.Ok(body);
            });
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

    /// <summary>Reward multipliers — boost/dampen earnings by window, schedule, or role (admin). Complex windows
    /// are easier on the web Multipliers page; these cover the common cases.</summary>
    [SubSlashCommand("multiplier", "Reward multipliers: windows, recurring schedules, and role boosts.")]
    public class MultiplierModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
    {
        [SubSlashCommand("list", "List this server's reward multipliers.")]
        public Task ListAsync()
            => RunAsync(async (sp, guildId) =>
            {
                var rows = await sp.GetRequiredService<RewardMultiplierCommandService>().ListAsync(guildId);
                if (rows.Count == 0)
                {
                    return CommandResult.Ok("No reward multipliers configured. Add one with `/track multiplier window|recurring|role`.");
                }

                var lines = rows.Select(m => $"- **{m.Name}** ×{m.Factor:0.##} — {m.Kind}, {(m.Enabled ? "on" : "off")}");
                return CommandResult.Ok("**Reward multipliers**\n" + string.Join("\n", lines));
            }, RequiredRole.Admin);

        [SubSlashCommand("role", "Add an always-on multiplier for members holding a role.")]
        public Task RoleAsync(
            [SlashCommandParameter(Name = "role", Description = "Role whose members earn the multiplier")] Role role,
            [SlashCommandParameter(Name = "factor", Description = "Multiplier (e.g. 1.5, 2). >1 boosts, <1 dampens")] double factor,
            [SlashCommandParameter(Name = "name", Description = "A label for this rule")] string? name = null,
            [SlashCommandParameter(Name = "applies-to", Description = "Which rewards it affects (default all)")] MultiplierScope scope = MultiplierScope.All)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<RewardMultiplierCommandService>()
                    .AddRoleAsync(guildId, name ?? $"{role.Name} role", (decimal)factor, scope, role.Id),
                RequiredRole.Admin, auditAction: "track.multiplier.add");

        [SubSlashCommand("recurring", "Add a weekly recurring multiplier (guild-local times; may wrap past midnight).")]
        public Task RecurringAsync(
            [SlashCommandParameter(Name = "factor", Description = "Multiplier (e.g. 1.5, 2)")] double factor,
            [SlashCommandParameter(Name = "from", Description = "Start time HH:mm (guild time)")] string from,
            [SlashCommandParameter(Name = "to", Description = "End time HH:mm")] string to,
            [SlashCommandParameter(Name = "mon", Description = "Active on Mondays")] bool mon = false,
            [SlashCommandParameter(Name = "tue", Description = "Active on Tuesdays")] bool tue = false,
            [SlashCommandParameter(Name = "wed", Description = "Active on Wednesdays")] bool wed = false,
            [SlashCommandParameter(Name = "thu", Description = "Active on Thursdays")] bool thu = false,
            [SlashCommandParameter(Name = "fri", Description = "Active on Fridays")] bool fri = false,
            [SlashCommandParameter(Name = "sat", Description = "Active on Saturdays")] bool sat = false,
            [SlashCommandParameter(Name = "sun", Description = "Active on Sundays")] bool sun = false,
            [SlashCommandParameter(Name = "name", Description = "A label for this rule")] string? name = null,
            [SlashCommandParameter(Name = "applies-to", Description = "Which rewards it affects (default all)")] MultiplierScope scope = MultiplierScope.All)
            => RunAsync((sp, guildId) =>
            {
                if (!TimeOnly.TryParse(from, out var f) || !TimeOnly.TryParse(to, out var t))
                {
                    return Task.FromResult(CommandResult.Error("Enter times as HH:mm (e.g. 19:00)."));
                }

                var days = (mon ? WeekDays.Monday : 0) | (tue ? WeekDays.Tuesday : 0) | (wed ? WeekDays.Wednesday : 0)
                    | (thu ? WeekDays.Thursday : 0) | (fri ? WeekDays.Friday : 0) | (sat ? WeekDays.Saturday : 0) | (sun ? WeekDays.Sunday : 0);

                return sp.GetRequiredService<RewardMultiplierCommandService>()
                    .AddRecurringAsync(guildId, name ?? "Recurring", (decimal)factor, scope, days, f, t);
            }, RequiredRole.Admin, auditAction: "track.multiplier.add");

        [SubSlashCommand("window", "Add a one-off multiplier window (e.g. happy hour). Times are in your time zone.")]
        public Task WindowAsync(
            [SlashCommandParameter(Name = "factor", Description = "Multiplier (e.g. 1.5, 2)")] double factor,
            [SlashCommandParameter(Name = "start", Description = "When it begins, your time zone (e.g. 2026-06-01 19:00)")] string start,
            [SlashCommandParameter(Name = "end", Description = "When it ends")] string end,
            [SlashCommandParameter(Name = "name", Description = "A label for this rule")] string? name = null,
            [SlashCommandParameter(Name = "applies-to", Description = "Which rewards it affects (default all)")] MultiplierScope scope = MultiplierScope.All)
            => RunAsync(async (sp, guildId) =>
            {
                var tz = sp.GetRequiredService<TimeZoneService>();
                var (okStart, startUtc, errStart) = await tz.ParseLocalAsync(guildId, Context.User.Id, start);
                if (!okStart)
                {
                    return CommandResult.Error(errStart ?? "Couldn't read the start time.");
                }

                var (okEnd, endUtc, errEnd) = await tz.ParseLocalAsync(guildId, Context.User.Id, end);
                if (!okEnd)
                {
                    return CommandResult.Error(errEnd ?? "Couldn't read the end time.");
                }

                return await sp.GetRequiredService<RewardMultiplierCommandService>()
                    .AddOneOffAsync(guildId, name ?? "Window", (decimal)factor, scope, startUtc!.Value, endUtc!.Value);
            }, RequiredRole.Admin, auditAction: "track.multiplier.add");

        [SubSlashCommand("enable", "Enable or disable a multiplier.")]
        public Task EnableAsync(
            [SlashCommandParameter(Name = "multiplier", Description = "Pick a multiplier",
                AutocompleteProviderType = typeof(MultiplierAutocompleteProvider))] string multiplier,
            [SlashCommandParameter(Name = "enabled", Description = "true to enable, false to disable")] bool enabled = true)
            => RunAsync((sp, guildId) => Guid.TryParse(multiplier, out var id)
                ? sp.GetRequiredService<RewardMultiplierCommandService>().SetEnabledAsync(guildId, id, enabled)
                : Task.FromResult(CommandResult.Error("Pick a multiplier from the list.")),
                RequiredRole.Admin, auditAction: "track.multiplier.toggle");

        [SubSlashCommand("remove", "Remove a multiplier.")]
        public Task RemoveAsync(
            [SlashCommandParameter(Name = "multiplier", Description = "Pick a multiplier",
                AutocompleteProviderType = typeof(MultiplierAutocompleteProvider))] string multiplier)
            => RunAsync((sp, guildId) => Guid.TryParse(multiplier, out var id)
                ? sp.GetRequiredService<RewardMultiplierCommandService>().RemoveAsync(guildId, id)
                : Task.FromResult(CommandResult.Error("Pick a multiplier from the list.")),
                RequiredRole.Admin, auditAction: "track.multiplier.remove");
    }

    /// <summary>Tracking reward settings (admin) — the scalar config that otherwise lives only on the web page.</summary>
    [SubSlashCommand("settings", "Session guards, limits, and multiplier/bonus policy.")]
    public class SettingsModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
    {
        [SubSlashCommand("guards", "Whether new sessions pause reward time while muted/alone by default.")]
        public Task GuardsAsync(
            [SlashCommandParameter(Name = "apply", Description = "true = pause muted/alone time; false = count all presence")] bool apply)
            => RunAsync((sp, guildId) => sp.GetRequiredService<ConfigCommandService>().SetApplyGuardsToSessionsAsync(guildId, apply),
                RequiredRole.Admin, auditAction: "config.tracking");

        [SubSlashCommand("limits", "Session safety limits (omit a value to leave it unchanged).")]
        public Task LimitsAsync(
            [SlashCommandParameter(Name = "max-session-hours", Description = "Auto-close a never-stopped session (0 = off)")] int? maxSessionHours = null,
            [SlashCommandParameter(Name = "activity-retention-days", Description = "Prune raw activity rows older than N days (0 = forever)")] int? activityRetentionDays = null,
            [SlashCommandParameter(Name = "min-session-seconds", Description = "Drop drive-by attendees under N seconds (0 = keep all)")] int? minSessionSeconds = null)
            => RunAsync(async (sp, guildId) =>
            {
                var config = sp.GetRequiredService<ConfigCommandService>();
                var messages = new List<string>();
                if (maxSessionHours is { } mh)
                {
                    messages.Add((await config.SetMaxSessionHoursAsync(guildId, mh)).Message);
                }

                if (activityRetentionDays is { } rd)
                {
                    messages.Add((await config.SetActivityRetentionAsync(guildId, rd)).Message);
                }

                if (minSessionSeconds is { } ms)
                {
                    messages.Add((await config.SetMinTrackedSecondsAsync(guildId, ms)).Message);
                }

                return CommandResult.Ok(messages.Count == 0 ? "Provide at least one limit to change." : string.Join("\n", messages));
            }, RequiredRole.Admin, auditAction: "config.tracking");

        [SubSlashCommand("multiplier-policy", "How multipliers stack + a global cap, and the session presence bonuses.")]
        public Task PolicyAsync(
            [SlashCommandParameter(Name = "stacking", Description = "How overlapping windows combine")] MultiplierStacking? stacking = null,
            [SlashCommandParameter(Name = "cap", Description = "Clamp the effective factor (0 = no cap)")] double? cap = null,
            [SlashCommandParameter(Name = "start-bonus", Description = "Flat POINTS for being present at the start")] int? startBonus = null,
            [SlashCommandParameter(Name = "end-bonus", Description = "Flat POINTS for staying to the end")] int? endBonus = null,
            [SlashCommandParameter(Name = "start-window-min", Description = "Joined within N min of start qualifies")] int? startWindowMinutes = null,
            [SlashCommandParameter(Name = "end-window-min", Description = "Present within N min of end qualifies")] int? endWindowMinutes = null,
            [SlashCommandParameter(Name = "scale-bonuses", Description = "Scale presence bonuses by the active multiplier")] bool? scaleBonuses = null)
            => RunAsync(async (sp, guildId) =>
            {
                // Merge with current settings so omitted options stay unchanged.
                var s = await sp.GetRequiredService<MusterDbContext>().GetSettingsAsync(guildId);
                return await sp.GetRequiredService<ConfigCommandService>().SetMultiplierSettingsAsync(
                    guildId,
                    stacking ?? s.MultiplierStacking,
                    cap is { } c ? (decimal)c : s.MultiplierCap,
                    startBonus ?? s.SessionStartBonus,
                    endBonus ?? s.SessionEndBonus,
                    startWindowMinutes ?? s.StartBonusWindowMinutes,
                    endWindowMinutes ?? s.EndBonusWindowMinutes,
                    scaleBonuses ?? s.MultiplyPresenceBonuses);
            }, RequiredRole.Admin, auditAction: "config.tracking");
    }
}
