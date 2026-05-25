using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>
/// Discord adapter for the unified quest board. Guild quests (minted) and personal quests (escrowed
/// from the poster's balance) share one command set; <see cref="QuestBoardService"/> routes each
/// action by the quest's origin. Dates are entered in the caller's time zone (set with /timezone).
/// </summary>
public class QuestModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("quest-post", "Post a quest: a guild quest mints its reward, or a personal quest escrows your own balance.")]
    public Task<Reply> PostAsync(
        [SlashCommandParameter(Name = "name", Description = "Quest name")] string name,
        [SlashCommandParameter(Name = "currency", Description = "Reward currency", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency,
        [SlashCommandParameter(Name = "reward", Description = "Reward amount")] long reward,
        [SlashCommandParameter(Name = "type", Description = "Guild quest (minted) or personal quest (escrowed from your balance)")] QuestKind type = QuestKind.Guild,
        [SlashCommandParameter(Name = "description", Description = "What to do")] string description = "",
        [SlashCommandParameter(Name = "starts", Description = "When it opens, in your time zone, e.g. 2026-06-01 18:00 (optional)")] string starts = "",
        [SlashCommandParameter(Name = "expires", Description = "When it closes, in your time zone, e.g. 2026-06-08 18:00 (optional)")] string expires = "",
        [SlashCommandParameter(Name = "tier", Description = "Difficulty tier for a guild quest — sets the bonus POINTS from guild config")] QuestTier tier = QuestTier.None)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>()
                .PostParsedAsync(guildId, Context.User.Id, type, name, currency, reward, description, starts, expires, tier),
            auditAction: "quest.post");

    [SlashCommand("quest-list", "List the open quest board.")]
    public Task<Reply> ListAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestBoardService>().ListAsync(guildId));

    [SlashCommand("quest-claim", "Claim a quest to work on it.")]
    public Task<Reply> ClaimAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestBoardService>().ClaimAsync(guildId, quest, Context.User.Id));

    [SlashCommand("quest-submit", "Submit a quest you've completed.")]
    public Task<Reply> SubmitAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestBoardService>().SubmitAsync(guildId, quest, Context.User.Id));

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

    [SlashCommand("quest-notarize", "Approve a submitted personal quest with a difficulty tier, granting bonus POINTS (Quest Manager).")]
    public Task<Reply> NotarizeAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "tier", Description = "Difficulty tier — sets the bonus POINTS from guild config")] QuestTier tier)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>().NotarizeAsync(guildId, quest, tier, Context.User.Id),
            RequiredRole.QuestManager, "quest.notarize");

    [SlashCommand("quest-arbitrate", "Resolve a disputed personal quest (Quest Manager).")]
    public Task<Reply> ArbitrateAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "pay", Description = "Pay the completer (true) or refund the owner (false)")] bool pay)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestBoardService>().ArbitrateAsync(guildId, quest, pay),
            RequiredRole.QuestManager, "quest.arbitrate");
}
