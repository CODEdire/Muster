using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the quest board. Logic lives in <see cref="QuestCommandService"/>.</summary>
public class QuestModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("quest-post", "Post a guild quest (its reward is minted on approval).")]
    public Task<string> PostAsync(
        [SlashCommandParameter(Name = "name", Description = "Quest name")] string name,
        [SlashCommandParameter(Name = "currency", Description = "Reward currency", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency,
        [SlashCommandParameter(Name = "reward", Description = "Reward amount on approval")] long reward,
        [SlashCommandParameter(Name = "description", Description = "What to do")] string description = "")
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestCommandService>().PostGuildQuestAsync(guildId, Context.User.Id, name, description, currency, reward),
            RequiredRole.QuestManager,
            auditAction: "quest.post");

    [SlashCommand("quest-list", "List open quests.")]
    public Task<string> ListAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestCommandService>().ListAsync(guildId));

    [SlashCommand("quest-claim", "Claim a quest to work on it.")]
    public Task<string> ClaimAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest id")] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestCommandService>().ClaimAsync(guildId, quest, Context.User.Id));

    [SlashCommand("quest-submit", "Submit a claimed quest for approval.")]
    public Task<string> SubmitAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest id")] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestCommandService>().SubmitAsync(guildId, quest, Context.User.Id));

    [SlashCommand("quest-approve", "Approve a member's quest submission and award them.")]
    public Task<string> ApproveAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest id")] string quest,
        [SlashCommandParameter(Name = "member", Description = "Member to approve")] User member)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestCommandService>().ApproveAsync(guildId, quest, member.Id, Context.User.Id),
            RequiredRole.QuestManager,
            auditAction: "quest.approve");
}
