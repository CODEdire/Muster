using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the award command. All logic lives in <see cref="AwardCommandService"/>.</summary>
public class AwardModule(IServiceScopeFactory scopeFactory) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("award", "Award points to a member for an off-platform contribution.")]
    public async Task<string> AwardAsync(
        [SlashCommandParameter(Name = "member", Description = "Member to award")] User member,
        [SlashCommandParameter(Name = "amount", Description = "Points to award")] long amount,
        [SlashCommandParameter(Name = "reason", Description = "Why they're being awarded")] string reason)
    {
        if (Context.Guild is not { } guild)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<AwardCommandService>();
        var result = await commands.AwardPointsAsync(guild.Id, Context.User.Id, member.Id, amount, reason);
        return result.Message;
    }
}
