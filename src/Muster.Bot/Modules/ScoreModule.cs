using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the score commands (open to all). Logic in <see cref="ScoreCommandService"/>.</summary>
public class ScoreModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("leaderboard", "Show the current season points leaderboard.")]
    public Task<string> LeaderboardAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<ScoreCommandService>().LeaderboardAsync(guildId));

    [SlashCommand("wallet", "Show your currency balances.")]
    public Task<string> WalletAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<ScoreCommandService>().WalletAsync(guildId, Context.User.Id));
}
