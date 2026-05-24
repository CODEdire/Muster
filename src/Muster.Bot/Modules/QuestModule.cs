using Microsoft.Extensions.DependencyInjection;
using Muster.Bot.Autocomplete;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Whether a posted quest is funded by the guild (minted) or by the poster's own balance (escrowed).</summary>
public enum QuestKind
{
    Guild,
    Personal,
}

/// <summary>Discord adapter for the quest board. Logic lives in <see cref="QuestCommandService"/>.</summary>
public class QuestModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("quest-post", "Post a quest: a guild quest mints its reward, or a personal bounty escrows your own balance.")]
    public Task<Reply> PostAsync(
        [SlashCommandParameter(Name = "name", Description = "Quest name")] string name,
        [SlashCommandParameter(Name = "currency", Description = "Reward currency", AutocompleteProviderType = typeof(CurrencyAutocompleteProvider))] string currency,
        [SlashCommandParameter(Name = "reward", Description = "Reward amount")] long reward,
        [SlashCommandParameter(Name = "type", Description = "Guild quest (minted) or personal bounty (escrowed from your balance)")] QuestKind type = QuestKind.Guild,
        [SlashCommandParameter(Name = "description", Description = "What to do")] string description = "",
        [SlashCommandParameter(Name = "expires-in-days", Description = "Auto-close/refund after this many days (optional)")] long expiresInDays = 0)
        => RunAsync(async (sp, guildId) =>
        {
            var deadline = expiresInDays > 0 ? DateTimeOffset.UtcNow.AddDays(expiresInDays) : (DateTimeOffset?)null;

            if (type == QuestKind.Guild)
            {
                var auth = sp.GetRequiredService<GuildAuthorizationService>();
                if (!await auth.IsQuestManagerAsync(guildId, Context.User.Id))
                {
                    return CommandResult.Error("You need to be a quest manager to post a guild quest. Choose `type: Personal` to fund one from your own balance.");
                }

                return await sp.GetRequiredService<QuestCommandService>()
                    .PostGuildQuestAsync(guildId, Context.User.Id, name, description, currency, reward, deadline);
            }

            return await sp.GetRequiredService<BountyCommandService>()
                .PostAsync(guildId, Context.User.Id, name, currency, reward, description, deadline);
        }, auditAction: "quest.post");

    [SlashCommand("quest-list", "List open quests.")]
    public Task<Reply> ListAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestCommandService>().ListAsync(guildId));

    [SlashCommand("quest-claim", "Claim a quest to work on it.")]
    public Task<Reply> ClaimAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestCommandService>().ClaimAsync(guildId, quest, Context.User.Id));

    [SlashCommand("quest-submit", "Submit a claimed quest for approval.")]
    public Task<Reply> SubmitAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest)
        => RunAsync((sp, guildId) => sp.GetRequiredService<QuestCommandService>().SubmitAsync(guildId, quest, Context.User.Id));

    [SlashCommand("quest-approve", "Approve a member's quest submission and award them.")]
    public Task<Reply> ApproveAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest", AutocompleteProviderType = typeof(QuestAutocompleteProvider))] string quest,
        [SlashCommandParameter(Name = "member", Description = "Member to approve")] User member)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<QuestCommandService>().ApproveAsync(guildId, quest, member.Id, Context.User.Id),
            RequiredRole.QuestManager,
            auditAction: "quest.approve");
}
