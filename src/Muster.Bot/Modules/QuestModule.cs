using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the quest board. Logic lives in <see cref="QuestCommandService"/>.</summary>
public class QuestModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("quest-post", "Post a new quest to the board.")]
    public Task<string> PostAsync(
        [SlashCommandParameter(Name = "name", Description = "Quest name")] string name,
        [SlashCommandParameter(Name = "description", Description = "What to do")] string description,
        [SlashCommandParameter(Name = "reward", Description = "Points on approval")] long reward)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestCommandService>().PostAsync(guildId, Context.User.Id, name, description, reward),
            RequiredRole.Officer,
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
            RequiredRole.Officer,
            auditAction: "quest.approve");
}
