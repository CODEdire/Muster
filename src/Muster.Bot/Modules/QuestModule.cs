using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Commands.Quests;

namespace Muster.Bot.Modules;

/// <summary>
/// Discord adapter for the unified quest board. Guild quests (minted) and personal quests (escrowed
/// from the poster's balance) share one command set; <see cref="QuestBoardService"/> routes each
/// action by the quest's origin. Dates are entered in the caller's time zone (set with /timezone).
/// </summary>
public class QuestModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [SlashCommand("quest-post", "Post a quest: a guild quest mints its reward, or a personal quest escrows your own balance.")]
    public Task<Reply> PostAsync(
        [SlashCommandParameter(Name = "name", Description = "Quest name")] string name,
        [SlashCommandParameter(Name = "currency", Description = "Reward currency", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency,
        [SlashCommandParameter(Name = "reward", Description = "Reward amount")] long reward,
        [SlashCommandParameter(Name = "type", Description = "Guild quest (minted) or personal quest (escrowed from your balance)")] QuestKind type = QuestKind.Guild,
        [SlashCommandParameter(Name = "description", Description = "What to do")] string description = "",
        [SlashCommandParameter(Name = "starts", Description = "When it opens, in your time zone, e.g. 2026-06-01 18:00 (optional)")] string starts = "",
        [SlashCommandParameter(Name = "expires", Description = "When it closes, in your time zone, e.g. 2026-06-08 18:00 (optional)")] string expires = "",
        [SlashCommandParameter(Name = "tier", Description = "Difficulty tier for a guild quest — sets the bonus POINTS from guild config")] QuestTier tier = QuestTier.None,
        [SlashCommandParameter(Name = "require_final_approval", Description = "Personal quest: ask a manager to give a final sign-off before payout")] bool requireFinalApproval = false,
        [SlashCommandParameter(Name = "repeatable", Description = "Guild quest: stays open for repeated completions instead of closing after the first")] bool repeatable = false,
        [SlashCommandParameter(Name = "slots", Description = "Guild quest: how many members can complete it (capacity, default 1)")] long slots = 1)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>()
                .PostParsedAsync(guildId, Context.User.Id, type, name, currency, reward, description, starts, expires, tier, requireFinalApproval, repeatable, (int)Math.Max(1, slots)),
            auditAction: "quest.post");

    [SlashCommand("quest-edit", "Edit a quest before anyone claims it (owner for personal, manager for guild).")]
    public Task<Reply> EditAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "name", Description = "New name")] string name = "",
        [SlashCommandParameter(Name = "description", Description = "New description")] string description = "",
        [SlashCommandParameter(Name = "reward", Description = "Guild quest: new reward amount")] long reward = 0,
        [SlashCommandParameter(Name = "slots", Description = "Guild quest: new capacity")] long slots = 0)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>().EditAsync(
                guildId, quest, Context.User.Id, NullIfBlank(name), NullIfBlank(description),
                reward > 0 ? reward : null, null, null, slots > 0 ? (int)slots : null),
            auditAction: "quest.edit");

    [SlashCommand("quest-list", "List the open quest board.")]
    public Task<Reply> ListAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestBoardService>().ListAsync(guildId));

    [SlashCommand("quest-claim", "Claim a quest to work on it.")]
    public Task<Reply> ClaimAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestBoardService>().ClaimAsync(guildId, quest, Context.User.Id));

    [SlashCommand("quest-submit", "Submit a quest you've completed.")]
    public Task<Reply> SubmitAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "note", Description = "Optional note for the reviewer")] string note = "")
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestBoardService>().SubmitAsync(guildId, quest, Context.User.Id, NullIfBlank(note)));

    [SlashCommand("quest-revise", "Send a submitted quest back to the worker to revise (owner for personal, manager for guild).")]
    public Task<Reply> ReviseAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "member", Description = "For a guild quest, whose submission to send back")] User? member = null,
        [SlashCommandParameter(Name = "note", Description = "What to fix")] string note = "")
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>().RequestRevisionAsync(guildId, quest, Context.User.Id, member?.Id, NullIfBlank(note)),
            auditAction: "quest.revise");

    [SlashCommand("quest-approve", "Approve a member's guild-quest submission and award them (Quest Manager).")]
    public Task<Reply> ApproveAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "member", Description = "Member to approve")] User member)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>().ApproveAsync(guildId, quest, member.Id, Context.User.Id),
            RequiredRole.QuestManager,
            auditAction: "quest.approve");

    [SlashCommand("quest-confirm", "Confirm your personal quest is complete and pay the completer.")]
    public Task<Reply> ConfirmAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestBoardService>().ConfirmAsync(guildId, quest, Context.User.Id));

    [SlashCommand("quest-cancel", "Cancel a quest (refunds escrow for personal quests).")]
    public Task<Reply> CancelAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestBoardService>().CancelAsync(guildId, quest, Context.User.Id));

    [SlashCommand("quest-dispute", "Raise a dispute on a submitted personal quest for a Quest Manager to review.")]
    public Task<Reply> DisputeAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestBoardService>().DisputeAsync(guildId, quest, Context.User.Id));

    [SlashCommand("quest-accept", "Accept a pending personal quest at intake and set its difficulty tier (Quest Manager).")]
    public Task<Reply> AcceptIntakeAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "tier", Description = "Difficulty tier — sets the bonus POINTS from guild config")] QuestTier tier = QuestTier.None,
        [SlashCommandParameter(Name = "require_final_approval", Description = "Require a final manager sign-off before payout (if the guild lets the approver decide)")] bool requireFinalApproval = false)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>().AcceptIntakeAsync(guildId, quest, tier, requireFinalApproval, Context.User.Id),
            RequiredRole.QuestManager, "quest.accept");

    [SlashCommand("quest-reject", "Reject a pending personal quest at intake and refund the owner (Quest Manager).")]
    public Task<Reply> RejectIntakeAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>().RejectIntakeAsync(guildId, quest, Context.User.Id),
            RequiredRole.QuestManager, "quest.rejectIntake");

    [SlashCommand("quest-finalize", "Give the final sign-off on a personal quest: pay the completer or refund the owner (Quest Manager).")]
    public Task<Reply> FinalizeAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "pay", Description = "Pay the completer (true) or refund the owner (false)")] bool pay)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>().FinalizeAsync(guildId, quest, pay, Context.User.Id),
            RequiredRole.QuestManager, "quest.finalize");

    [SlashCommand("quest-arbitrate", "Resolve a disputed personal quest (Quest Manager).")]
    public Task<Reply> ArbitrateAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "pay", Description = "Pay the completer (true) or refund the owner (false)")] bool pay)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>().ArbitrateAsync(guildId, quest, pay),
            RequiredRole.QuestManager, "quest.arbitrate");
}
