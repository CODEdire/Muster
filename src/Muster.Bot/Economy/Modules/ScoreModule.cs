using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services.Currencies;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Economy.Modules;

/// <summary>Discord adapter for the season points leaderboard (open to all). The voice-participation
/// leaderboard lives under <c>/track leaderboard</c>.</summary>
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
