using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Tracking;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for leaderboards (open to all): season points and voice participation time.</summary>
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

    [SlashCommand("voice-leaderboard", "Show the top members by voice participation time.")]
    public Task VoiceLeaderboardAsync()
        => RunAsync(async (sp, guildId) =>
        {
            var board = await sp.GetRequiredService<ParticipationReadService>().VoiceLeaderboardAsync(guildId, 10);
            if (board.Count == 0)
            {
                return CommandResult.Ok("No voice activity tracked yet.");
            }

            var lines = board.Select((e, i) => $"{i + 1}. <@{e.UserId}> — **{FormatDuration(e.VoiceMinutes)}**");
            return CommandResult.Ok("**Voice participation**\n" + string.Join("\n", lines));
        });

    private static string FormatDuration(int minutes)
    {
        var hours = minutes / 60;
        var mins = minutes % 60;
        return hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
    }
}
