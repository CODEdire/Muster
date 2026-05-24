using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

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
        var awards = scope.ServiceProvider.GetRequiredService<ManualAwardService>();
        await awards.AwardPointsAsync(guild.Id, member.Id, amount, reason, Context.User.Id);

        return $"Awarded **{amount}** points to <@{member.Id}> — {reason}";
    }
}
