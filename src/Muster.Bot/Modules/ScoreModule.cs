using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the score commands. Logic lives in <see cref="ScoreCommandService"/>.</summary>
public class ScoreModule(IServiceScopeFactory scopeFactory) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("leaderboard", "Show the current season points leaderboard.")]
    public async Task<string> LeaderboardAsync()
    {
        if (Context.Guild is not { } guild)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<ScoreCommandService>();
        var result = await commands.LeaderboardAsync(guild.Id);
        return result.Message;
    }

    [SlashCommand("wallet", "Show your currency balances.")]
    public async Task<string> WalletAsync()
    {
        if (Context.Guild is not { } guild)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<ScoreCommandService>();
        var result = await commands.WalletAsync(guild.Id, Context.User.Id);
        return result.Message;
    }
}
