using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Quests;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Quests;
using Wolverine;

namespace Muster.Bot.Questing.Modules;

/// <summary>Status lens for <c>/quest list</c> — mirrors the web board's tabs (Action Needed = manager review queue).</summary>
public enum QuestListTab
{
    Active,
    ActionNeeded,
    History,
}

/// <summary>Scope filter for <c>/quest list</c> — All / Guild (system duties) / Player (any author) / Mine (bounties I posted + anything I claimed).</summary>
public enum QuestTypeFilter
{
    All,
    Guild,
    Player,
    Mine,
}

/// <summary>
/// Discord adapter for the unified quest board, organized under the <c>/quest</c> root with sub-commands that
/// mirror the web UI / public API surface. Guild quests (minted) and personal quests (escrowed from the poster's
/// balance) share one command set; each action is dispatched as a CQRS command via the bus, which routes by origin
/// and authorizes — the same funnel as the web and API. Dates are entered in the caller's time zone (set with
/// <c>/timezone</c>). Manager-only review/intake actions live in the <c>review</c> and <c>intake</c> sub-groups.
/// </summary>
[SlashCommand("quest", "Quest board: post, claim, submit, review and resolve quests.")]
public class QuestModule(IServiceScopeFactory scopeFactory) : QuestModuleBase(scopeFactory)
{
    // ---- Reads -------------------------------------------------------------

    [SubSlashCommand("list", "Browse the quest board (active, yours, or history).")]
    public Task ListAsync(
        [SlashCommandParameter(Name = "tab", Description = "Which quests to show")] QuestListTab tab = QuestListTab.Active,
        [SlashCommandParameter(Name = "type", Description = "Filter by guild or player quests")] QuestTypeFilter type = QuestTypeFilter.All,
        [SlashCommandParameter(Name = "search", Description = "Filter by name or description")] string search = "")
        => RunAsync(async (sp, guildId) =>
        {
            var isManager = await sp.GetRequiredService<GuildAuthorizationService>().IsQuestManagerAsync(guildId, Context.User.Id);
            var tabKey = tab switch { QuestListTab.ActionNeeded => "actionneeded", QuestListTab.History => "history", _ => "active" };
            var typeKey = type switch { QuestTypeFilter.Guild => "guild", QuestTypeFilter.Player => "player", QuestTypeFilter.Mine => "mine", _ => (string?)null };

            var board = await sp.GetRequiredService<IQuestReadService>()
                .GetBoardAsync(guildId, Context.User.Id, isManager, tabKey, typeKey, NullIfBlank(search), "created", desc: true, page: 1, pageSize: 25);

            if (board.Items.Count == 0)
            {
                return CommandResult.Ok("No quests match. Post one with `/quest post`.");
            }

            var lines = board.Items.Select(i =>
            {
                var kind = i.Origin == QuestOrigin.Guild ? "Guild" : "Player";
                var code = board.Codes.GetValueOrDefault(i.RewardCurrencyId, "?");
                var until = i.Deadline is { } d ? $" · closes <t:{d.ToUnixTimeSeconds()}:R>" : "";
                return $"• **{i.Name}** _[{kind}]_ — {i.RewardAmount} {code} ({i.Status}){until}";
            });
            var header = $"**Quest board** · {board.Total} total (page {board.Page}/{board.TotalPages})\nDetails: `/quest show`.";
            return CommandResult.Ok(header + "\n" + string.Join("\n", lines));
        });

    [SubSlashCommand("show", "Show a quest's details: status, reward, and participants.")]
    public Task ShowAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync(async (sp, guildId) =>
        {
            if (!Guid.TryParse(quest, out var questId))
            {
                return CommandResult.Error("That doesn't look like a valid quest id.");
            }

            var q = await sp.GetRequiredService<IQuestReadService>().GetQuestDetailAsync(guildId, questId);
            if (q is null)
            {
                return CommandResult.Error("Quest not found.");
            }

            // Scrub private fields for non-privileged viewers — owner/manager/the row's own worker keep visibility.
            var isManager = await sp.GetRequiredService<GuildAuthorizationService>().IsQuestManagerAsync(guildId, Context.User.Id);
            q = QuestDetailViewScrub.ForViewer(q, Context.User.Id, isManager);

            var kind = q.Origin == QuestOrigin.Guild ? "Guild" : "Player";
            var owner = q.Origin == QuestOrigin.Guild ? "Guild issued" : q.OwnerName;
            var tier = q.Tier == QuestTier.None ? "" : $" · tier {q.Tier} (+{q.BonusPoints} pts)";
            var deadline = q.Deadline is { } d ? $" · closes <t:{d.ToUnixTimeSeconds()}:R>" : "";

            var lines = new List<string>
            {
                $"**{q.Name}** _[{kind}]_ — {q.RewardAmount} {q.RewardCode}{tier}",
                $"Status: {q.Status} · by {owner}{deadline}",
            };
            if (!string.IsNullOrWhiteSpace(q.Description))
            {
                lines.Add($"> {q.Description}");
            }

            if (q.Participants.Count > 0)
            {
                lines.Add("**Participants**");
                lines.AddRange(q.Participants.Select(p =>
                {
                    var by = p.ReviewedByName is { } rn ? $" — reviewed by {rn}" : "";
                    return $"• {p.Name}: {p.Status}{by}";
                }));
            }

            return CommandResult.Ok(string.Join("\n", lines));
        });

    // ---- Lifecycle ---------------------------------------------------------

    // `post` and `edit` are split into guild/player sub-groups so each only shows the parameters that apply to
    // that origin (a guild quest's tier/slots vs a player quest's escrow + final-approval) — no "only applies to
    // guild quests" caveats on shared options.

    [SubSlashCommand("post", "Post a new quest.")]
    public class PostModule(IServiceScopeFactory scopeFactory) : QuestModuleBase(scopeFactory)
    {
        [SubSlashCommand("guild", "Post a guild quest — its reward is minted on approval.")]
        public Task GuildAsync(
            [SlashCommandParameter(Name = "name", Description = "Quest name")] string name,
            [SlashCommandParameter(Name = "currency", Description = "Reward currency", AutocompleteProviderType = typeof(QuestCurrencyAutocompleteProvider))] string currency,
            [SlashCommandParameter(Name = "reward", Description = "Reward amount")] long reward,
            [SlashCommandParameter(Name = "description", Description = "What to do")] string description = "",
            [SlashCommandParameter(Name = "starts", Description = "When it opens — your local time (set /timezone), or `<t:...>` / `in 3 days` (optional)")] string starts = "",
            [SlashCommandParameter(Name = "expires", Description = "When it closes — your local time (set /timezone), or `<t:...>` / `in 3 days` (optional)")] string expires = "",
            [SlashCommandParameter(Name = "tier", Description = "Difficulty tier — sets the bonus POINTS from guild config")] QuestTier tier = QuestTier.None,
            [SlashCommandParameter(Name = "slots", Description = "How many members can complete it (capacity, default 1)")] long slots = 1)
            => PostAsync(QuestOrigin.Guild, name, currency, reward, description, starts, expires, tier, requireFinalApproval: false, slots);

        [SubSlashCommand("player", "Post a player quest — its reward is escrowed from your own balance.")]
        public Task PlayerAsync(
            [SlashCommandParameter(Name = "name", Description = "Quest name")] string name,
            [SlashCommandParameter(Name = "currency", Description = "Reward currency", AutocompleteProviderType = typeof(QuestCurrencyAutocompleteProvider))] string currency,
            [SlashCommandParameter(Name = "reward", Description = "Reward amount")] long reward,
            [SlashCommandParameter(Name = "description", Description = "What to do")] string description = "",
            [SlashCommandParameter(Name = "starts", Description = "When it opens — your local time (set /timezone), or `<t:...>` / `in 3 days` (optional)")] string starts = "",
            [SlashCommandParameter(Name = "expires", Description = "When it closes — your local time (set /timezone), or `<t:...>` / `in 3 days` (optional)")] string expires = "",
            [SlashCommandParameter(Name = "require_final_approval", Description = "Ask a manager to give a final sign-off before payout")] bool requireFinalApproval = false)
            => PostAsync(QuestOrigin.Player, name, currency, reward, description, starts, expires, QuestTier.None, requireFinalApproval, slots: 1);

        private Task PostAsync(QuestOrigin origin, string name, string currency, long reward, string description,
            string starts, string expires, QuestTier tier, bool requireFinalApproval, long slots)
            => RunAsync(async (sp, guildId) =>
            {
                // Date parsing is an adapter concern (interprets the user's local time) — the command carries UTC.
                var tz = sp.GetRequiredService<TimeZoneService>();
                var (startOk, startsAt, startErr) = await tz.ParseLocalAsync(guildId, Context.User.Id, NullIfBlank(starts));
                if (!startOk)
                {
                    return CommandResult.Error(startErr!);
                }

                var (expiresOk, deadline, expiresErr) = await tz.ParseLocalAsync(guildId, Context.User.Id, NullIfBlank(expires));
                if (!expiresOk)
                {
                    return CommandResult.Error(expiresErr!);
                }

                var command = new PostQuest(guildId, Context.User.Id, origin, name, currency, reward, description,
                    startsAt, deadline, tier, requireFinalApproval, (int)Math.Max(1, slots));
                var result = await sp.GetRequiredService<IMessageBus>().InvokeAsync<Result>(command);
                return result.ToCommandResult($"Quest **{name}** posted.");
            });
    }

    [SubSlashCommand("edit", "Edit a quest before anyone claims it.")]
    public class EditModule(IServiceScopeFactory scopeFactory) : QuestModuleBase(scopeFactory)
    {
        [SubSlashCommand("guild", "Edit a guild quest before it's claimed (Quest Manager).")]
        public Task GuildAsync(
            [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
            [SlashCommandParameter(Name = "name", Description = "New name")] string name = "",
            [SlashCommandParameter(Name = "description", Description = "New description")] string description = "",
            [SlashCommandParameter(Name = "reward", Description = "New reward amount")] long reward = 0,
            [SlashCommandParameter(Name = "tier", Description = "New difficulty tier")] QuestTier tier = QuestTier.None,
            [SlashCommandParameter(Name = "slots", Description = "New capacity")] long slots = 0)
            => Dispatch(quest, (g, id) => new EditQuest(g, id, Context.User.Id, NullIfBlank(name), NullIfBlank(description),
                reward > 0 ? reward : null, null, tier == QuestTier.None ? null : tier, slots > 0 ? (int)slots : null), "Quest updated.");

        [SubSlashCommand("player", "Edit your player quest before it's claimed.")]
        public Task PlayerAsync(
            [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
            [SlashCommandParameter(Name = "name", Description = "New name")] string name = "",
            [SlashCommandParameter(Name = "description", Description = "New description")] string description = "")
            => Dispatch(quest, (g, id) => new EditQuest(g, id, Context.User.Id, NullIfBlank(name), NullIfBlank(description),
                null, null, null, null), "Quest updated.");
    }

    [SubSlashCommand("cancel", "Cancel a quest (refunds escrow for player quests).")]
    public Task CancelAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => Dispatch(quest, (g, id) => new CancelQuest(g, id, Context.User.Id), "Quest cancelled (escrow refunded for a player quest).");

    [SubSlashCommand("claim", "Claim a quest to work on it.")]
    public Task ClaimAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => Dispatch(quest, (g, id) => new ClaimQuest(g, id, Context.User.Id), "Quest claimed. Use `/quest submit` when you've completed it.");

    [SubSlashCommand("submit", "Submit a quest you've completed.")]
    public Task SubmitAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "note", Description = "Optional note for the reviewer")] string note = "")
        => Dispatch(quest, (g, id) => new SubmitQuest(g, id, Context.User.Id, NullIfBlank(note)), "Quest submitted for review.");

    [SubSlashCommand("abandon", "Give up a quest you claimed — frees the slot for someone else.")]
    public Task AbandonAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => Dispatch(quest, (g, id) => new ReleaseQuestClaim(g, id, Context.User.Id, Context.User.Id),
            "You abandoned this quest — the slot is open again.");

    [SubSlashCommand("confirm", "Confirm your player quest is complete and pay the completer.")]
    public Task ConfirmAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => Dispatch(quest, (g, id) => new ConfirmQuest(g, id, Context.User.Id), "Confirmed — the completer is paid (or it's sent for final sign-off).");

    [SubSlashCommand("dispute", "Raise a dispute on a submitted player quest for a Quest Manager to review.")]
    public Task DisputeAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "reason", Description = "Why you're disputing (shown to the arbiter)")] string reason = "")
        => Dispatch(quest, (g, id) => new DisputeQuest(g, id, Context.User.Id, NullIfBlank(reason)), "Dispute raised — a Quest Manager will review it.");

    [SubSlashCommand("finalize", "Give the final sign-off on a player quest: pay the completer or refund the owner (Quest Manager).")]
    public Task FinalizeAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "pay", Description = "Pay the completer (true) or refund the owner (false)")] bool pay)
        => Dispatch(quest, (g, id) => new FinalizeQuest(g, id, Context.User.Id, pay),
            pay ? "Finalized — paid the completer (with any tier bonus)." : "Finalized — refunded the owner.", RequiredRole.QuestManager);

    [SubSlashCommand("arbitrate", "Resolve a disputed player quest (Quest Manager).")]
    public Task ArbitrateAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "pay", Description = "Pay the completer (true) or refund the owner (false)")] bool pay)
        => Dispatch(quest, (g, id) => new ArbitrateQuest(g, id, Context.User.Id, pay),
            pay ? "Resolved: paid the completer." : "Resolved: refunded the owner.", RequiredRole.QuestManager);

    // ---- Review: guild-quest submissions (Quest Manager / owner) -----------

    [SubSlashCommand("review", "Review guild-quest submissions: approve, reject, send back, or reopen.")]
    public class ReviewModule(IServiceScopeFactory scopeFactory) : QuestModuleBase(scopeFactory)
    {
        [SubSlashCommand("approve", "Approve a member's guild-quest submission and award them (Quest Manager).")]
        public Task ApproveAsync(
            [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
            [SlashCommandParameter(Name = "member", Description = "Member to approve")] User member)
            => Dispatch(quest, (g, id) => new ApproveQuestSubmission(g, id, member.Id, Context.User.Id),
                $"Approved <@{member.Id}>'s quest and awarded the reward.", RequiredRole.QuestManager);

        [SubSlashCommand("reject", "Reject a member's guild-quest submission — no reward (Quest Manager).")]
        public Task RejectAsync(
            [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
            [SlashCommandParameter(Name = "member", Description = "Member whose submission to reject")] User member,
            [SlashCommandParameter(Name = "note", Description = "Why it was rejected")] string note = "")
            => Dispatch(quest, (g, id) => new RejectQuestSubmission(g, id, member.Id, Context.User.Id, NullIfBlank(note)),
                $"Rejected <@{member.Id}>'s submission.", RequiredRole.QuestManager);

        [SubSlashCommand("revise", "Send a submission back to the worker to revise (owner for player, manager for guild).")]
        public Task ReviseAsync(
            [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
            [SlashCommandParameter(Name = "member", Description = "For a guild quest, whose submission to send back")] User? member = null,
            [SlashCommandParameter(Name = "note", Description = "What to fix")] string note = "")
            => Dispatch(quest, (g, id) => new RequestQuestRevision(g, id, Context.User.Id, member?.Id, NullIfBlank(note)),
                "Sent back to the worker to revise and resubmit.");

        [SubSlashCommand("reopen", "Reverse an accidental guild-quest rejection — restore the submission (Quest Manager).")]
        public Task ReopenAsync(
            [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
            [SlashCommandParameter(Name = "member", Description = "Member whose rejection to reverse")] User member)
            => Dispatch(quest, (g, id) => new ReopenQuestRejection(g, id, member.Id, Context.User.Id),
                $"Reopened <@{member.Id}>'s submission for review.", RequiredRole.QuestManager);

        [SubSlashCommand("release", "Force-release a member's claim, freeing the slot (Quest Manager).")]
        public Task ReleaseAsync(
            [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
            [SlashCommandParameter(Name = "member", Description = "Member whose claim to release")] User member)
            => Dispatch(quest, (g, id) => new ReleaseQuestClaim(g, id, Context.User.Id, member.Id),
                $"Released <@{member.Id}>'s claim — the slot is open again.", RequiredRole.QuestManager);
    }

    // ---- Intake: vet pending player quests (Quest Manager) -----------------

    [SubSlashCommand("intake", "Vet pending player quests before they open (Quest Manager).")]
    public class IntakeModule(IServiceScopeFactory scopeFactory) : QuestModuleBase(scopeFactory)
    {
        [SubSlashCommand("accept", "Accept a pending player quest and set its difficulty tier (Quest Manager).")]
        public Task AcceptAsync(
            [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
            [SlashCommandParameter(Name = "tier", Description = "Difficulty tier — sets the bonus POINTS from guild config")] QuestTier tier = QuestTier.None,
            [SlashCommandParameter(Name = "require_final_approval", Description = "Require a final manager sign-off before payout (if the guild lets the approver decide)")] bool requireFinalApproval = false)
            => Dispatch(quest, (g, id) => new AcceptQuestIntake(g, id, Context.User.Id, tier, requireFinalApproval),
                "Accepted and opened for takers.", RequiredRole.QuestManager);

        [SubSlashCommand("reject", "Reject a pending player quest at intake and refund the owner (Quest Manager).")]
        public Task RejectAsync(
            [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
            => Dispatch(quest, (g, id) => new RejectQuestIntake(g, id, Context.User.Id),
                "Rejected — the owner's escrow was refunded.", RequiredRole.QuestManager);
    }

    // ---- Config: the live channel board (Admin) ----------------------------

    [SubSlashCommand("config", "Configure the quest board (admin).")]
    public class ConfigModule(IServiceScopeFactory scopeFactory) : QuestModuleBase(scopeFactory)
    {
        [SubSlashCommand("channel", "Set the public channel the live quest board posts to — omit to turn it off (admin).")]
        public Task ChannelAsync(
            [SlashCommandParameter(Name = "channel", Description = "Text channel for the board (leave empty to disable)")] TextGuildChannel? channel = null)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<Muster.Infrastructure.Commands.Membership.ConfigCommandService>()
                    .SetQuestChannelAsync(guildId, channel?.Id ?? 0),
                RequiredRole.Admin, auditAction: "quest.config.channel");

        [SubSlashCommand("modchannel", "Set the private staff channel for intake/disputes/sign-off — omit to clear (admin).")]
        public Task ModChannelAsync(
            [SlashCommandParameter(Name = "channel", Description = "Private staff channel for mod-only states (leave empty to clear)")] TextGuildChannel? channel = null)
            => RunAsync(
                (sp, guildId) => sp.GetRequiredService<Muster.Infrastructure.Commands.Membership.ConfigCommandService>()
                    .SetQuestModChannelAsync(guildId, channel?.Id ?? 0),
                RequiredRole.Admin, auditAction: "quest.config.modchannel");
    }
}
