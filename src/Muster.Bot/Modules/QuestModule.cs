using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the quest board. Logic lives in <see cref="QuestCommandService"/>.</summary>
public class QuestModule(IServiceScopeFactory scopeFactory) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("quest-post", "Post a new quest to the board.")]
    public async Task<string> PostAsync(
        [SlashCommandParameter(Name = "name", Description = "Quest name")] string name,
        [SlashCommandParameter(Name = "description", Description = "What to do")] string description,
        [SlashCommandParameter(Name = "reward", Description = "Points on approval")] long reward)
        => await RunAsync(guild => guild.PostAsync(GuildId(), Context.User.Id, name, description, reward));

    [SlashCommand("quest-list", "List open quests.")]
    public async Task<string> ListAsync()
        => await RunAsync(commands => commands.ListAsync(GuildId()));

    [SlashCommand("quest-claim", "Claim a quest to work on it.")]
    public async Task<string> ClaimAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest id")] string quest)
        => await RunAsync(commands => commands.ClaimAsync(GuildId(), quest, Context.User.Id));

    [SlashCommand("quest-submit", "Submit a claimed quest for approval.")]
    public async Task<string> SubmitAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest id")] string quest)
        => await RunAsync(commands => commands.SubmitAsync(GuildId(), quest, Context.User.Id));

    [SlashCommand("quest-approve", "Approve a member's quest submission and award them.")]
    public async Task<string> ApproveAsync(
        [SlashCommandParameter(Name = "quest", Description = "Quest id")] string quest,
        [SlashCommandParameter(Name = "member", Description = "Member to approve")] User member)
        => await RunAsync(commands => commands.ApproveAsync(GuildId(), quest, member.Id, Context.User.Id));

    private ulong GuildId() => Context.Guild!.Id;

    private async Task<string> RunAsync(Func<QuestCommandService, Task<CommandResult>> action)
    {
        if (Context.Guild is null)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<QuestCommandService>();
        var result = await action(commands);
        return result.Message;
    }
}
