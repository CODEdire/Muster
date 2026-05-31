using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Platform;
using Muster.Contracts;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Membership;
using Muster.Infrastructure.Services.Musters;
using Muster.Persistence;
using Muster.Persistence.Queries;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Wolverine;

namespace Muster.Bot.Musters.Modules;

/// <summary>
/// Discord adapter for musters (reaction check-ins), under the <c>/muster</c> root. Each action is dispatched as a
/// CQRS command via the bus — the same funnel as the Check-In button and (later) the web — which authorizes the actor
/// and audits the action. Posting/closing only renders the card via the <c>MusterChanged</c> event the command
/// publishes; this adapter just resolves the target channel and reports the outcome.
/// </summary>
[SlashCommand("muster", "Reaction check-in musters: post, close, and configure.")]
public class MusterModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SubSlashCommand("post", "Post a check-in muster.")]
    public Task PostAsync(
        [SlashCommandParameter(Name = "prompt", Description = "What members are checking in for")] string prompt,
        [SlashCommandParameter(Name = "template", Description = "Reward template (required if you can't set custom rewards)",
            AutocompleteProviderType = typeof(MusterTemplateAutocompleteProvider))] string? template = null,
        [SlashCommandParameter(Name = "title", Description = "Optional heading shown above the prompt")] string title = "",
        [SlashCommandParameter(Name = "points", Description = "Participation points (managers only; overrides the template/default)")] long? points = null,
        [SlashCommandParameter(Name = "capacity", Description = "Max check-ins (optional; ignored when linked to a session)")] long capacity = 0,
        [SlashCommandParameter(Name = "expires-hours", Description = "Auto-close after this many hours (optional; 0 = guild default)")] long expiresHours = 0,
        [SlashCommandParameter(Name = "check-me-in", Description = "Check yourself in on create (default: guild setting)")] bool? checkMeIn = null,
        [SlashCommandParameter(Name = "channel", Description = "Channel to post to (default: configured muster channel, else here)",
            AutocompleteProviderType = typeof(MusterChannelAutocompleteProvider))] string? channel = null)
        => RunAsync(async (sp, guildId) =>
        {
            // Channel precedence: explicit override → configured muster channel → the channel the command was run in.
            var chosen = ulong.TryParse(channel, out var cid) ? cid : (ulong?)null;
            var configured = (await sp.GetRequiredService<GuildMusterSettingsService>().GetAsync(guildId)).MusterChannelId;
            var channelId = chosen ?? (configured != 0 ? configured : Context.Channel.Id);
            DateTimeOffset? expires = expiresHours > 0 ? DateTimeOffset.UtcNow.AddHours(expiresHours) : null;
            Guid? templateId = Guid.TryParse(template, out var tg) ? tg : null;

            var command = new CreateMuster(guildId, Context.User.Id, channelId,
                string.IsNullOrWhiteSpace(title) ? null : title.Trim(), prompt,
                TemplateId: templateId, Points: points, Coins: null, CoinCurrencyId: null,
                Capacity: capacity > 0 ? (int)capacity : null, ExpiresAt: expires, SessionId: null,
                CheckInCreator: checkMeIn);

            var result = await sp.GetRequiredService<IMessageBus>().InvokeAsync<Result<Guid>>(command);
            return MusterResultText.ToCommandResult(result, $"Muster posted in <#{channelId}>.");
        });

    [SubSlashCommand("close", "Close a muster — stops new check-ins.")]
    public Task CloseAsync(
        [SlashCommandParameter(Name = "muster", Description = "Muster to close",
            AutocompleteProviderType = typeof(MusterAutocompleteProvider))] string muster)
        => RunAsync(async (sp, guildId) =>
        {
            if (!Guid.TryParse(muster, out var musterId))
            {
                return CommandResult.Error("That doesn't look like a valid muster id.");
            }

            var result = await sp.GetRequiredService<IMessageBus>().InvokeAsync<Result>(new CloseMuster(guildId, Context.User.Id, musterId));
            return MusterResultText.ToCommandResult(result, "Muster closed.");
        });

    [SubSlashCommand("summary", "Show a muster's roster and status (ephemeral) — works for closed/archived ones too.")]
    public Task SummaryAsync(
        [SlashCommandParameter(Name = "muster", Description = "Muster to summarize",
            AutocompleteProviderType = typeof(RecentMusterAutocompleteProvider))] string muster)
        => RunAsync(async (sp, guildId) =>
        {
            if (!Guid.TryParse(muster, out var musterId))
            {
                return CommandResult.Error("That doesn't look like a valid muster id.");
            }

            var m = await sp.GetRequiredService<IMusterReadService>().GetDetailAsync(guildId, musterId);
            if (m is null)
            {
                return CommandResult.Error("Muster not found.");
            }

            var heading = string.IsNullOrWhiteSpace(m.Title) ? m.Prompt : m.Title;
            var count = m.Capacity is { } cap ? $"{m.Participants.Count}/{cap}" : m.Participants.Count.ToString();
            var rewardParts = new List<string>();
            if (m.Points > 0) { rewardParts.Add($"{m.Points} pts"); }
            if (m.Coins > 0) { rewardParts.Add($"{m.Coins} {m.CoinCode ?? "coins"}"); }
            var reward = rewardParts.Count > 0 ? $"\nReward: {string.Join(" + ", rewardParts)}" : "";
            var roster = m.Participants.Count == 0
                ? "_no check-ins_"
                : string.Join(" ", m.Participants.Take(50).Select(p => $"<@{p.UserId}>"))
                  + (m.Participants.Count > 50 ? $" …+{m.Participants.Count - 50}" : "");

            return CommandResult.Ok($"**{heading}** — {m.Status}\nChecked in: {count}{reward}\n{roster}");
        }, RequiredRole.TrackingManager);

    [SubSlashCommand("config", "Configure musters (admin).")]
    public class ConfigModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
    {
        [SubSlashCommand("channel", "Set the channel muster cards post to — omit to use the command's channel (admin).")]
        public Task ChannelAsync(
            [SlashCommandParameter(Name = "channel", Description = "Text channel for muster cards (leave empty to clear)")] TextGuildChannel? channel = null)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<ConfigCommandService>().SetMusterChannelAsync(guildId, channel?.Id ?? 0),
                RequiredRole.Admin, auditAction: "muster.config.channel");

        [SubSlashCommand("auto", "Toggle auto-creating a check-in muster (and coin gate) when a session opens (admin).")]
        public Task AutoAsync(
            [SlashCommandParameter(Name = "enabled", Description = "true = every new session posts a check-in muster and gates its coin")] bool enabled)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<ConfigCommandService>().SetAutoCreateMusterAsync(guildId, enabled),
                RequiredRole.Admin, auditAction: "muster.config.auto");
    }
}
