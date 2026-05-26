using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services.Currencies;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the season leaderboard (open to all). Reads the wallet-cache leaderboard via
/// <see cref="ICurrencyReadService"/>. Wallet balances live under <c>/currency balance</c>.</summary>
public class ScoreModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("leaderboard", "Show the current season points leaderboard.")]
    public Task LeaderboardAsync()
        => RunAsync(async (sp, guildId) =>
        {
            var board = await sp.GetRequiredService<ICurrencyReadService>().GetSeasonLeaderboardAsync(guildId, 10);
            if (board.Count == 0)
            {
                return CommandResult.Ok("No scores yet this season.");
            }

            var lines = board.Select((e, i) => $"{i + 1}. <@{e.UserId}> — **{e.Total:N0}**");
            return CommandResult.Ok("**Season leaderboard**\n" + string.Join("\n", lines));
        });
}
