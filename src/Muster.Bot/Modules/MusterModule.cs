using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the muster command. Logic lives in <see cref="MusterCommandService"/>.</summary>
public class MusterModule(IServiceScopeFactory scopeFactory) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("muster", "Post a reaction check-in that rewards members who react.")]
    public async Task<string> CreateAsync(
        [SlashCommandParameter(Name = "prompt", Description = "What members are checking in for")] string prompt,
        [SlashCommandParameter(Name = "emoji", Description = "Emoji to react with")] string emoji,
        [SlashCommandParameter(Name = "reward", Description = "Points per check-in")] long reward,
        [SlashCommandParameter(Name = "capacity", Description = "Max reactors (optional)")] long capacity = 0)
    {
        if (Context.Guild is not { } guild)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<MusterCommandService>();
        var result = await commands.CreateAsync(
            guild.Id, Context.Channel.Id, prompt, emoji, reward, capacity > 0 ? (int)capacity : null);
        return result.Message;
    }
}
