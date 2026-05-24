using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Services;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

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
        var queries = scope.ServiceProvider.GetRequiredService<ScoreQueryService>();
        var board = await queries.GetSeasonLeaderboardAsync(guild.Id, top: 10);

        if (board.Count == 0)
        {
            return "No scores yet this season.";
        }

        var lines = board.Select((e, i) => $"{i + 1}. <@{e.UserId}> — **{e.Total}**");
        return "**Season leaderboard**\n" + string.Join("\n", lines);
    }

    [SlashCommand("wallet", "Show your currency balances.")]
    public async Task<string> WalletAsync()
    {
        if (Context.Guild is not { } guild)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<ScoreQueryService>();
        var wallets = await queries.GetWalletsAsync(guild.Id, Context.User.Id);

        if (wallets.Count == 0)
        {
            return "You don't have any balances yet.";
        }

        return "**Your balances**\n" + string.Join("\n", wallets.Select(w => $"{w.CurrencyName}: **{w.Balance}**"));
    }
}
