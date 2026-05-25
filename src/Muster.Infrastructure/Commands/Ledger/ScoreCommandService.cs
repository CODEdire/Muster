
using Muster.Infrastructure.Services.Ledger;
namespace Muster.Infrastructure.Commands.Ledger;

/// <summary>Platform-independent logic for the score/leaderboard/wallet commands.</summary>
public class ScoreCommandService(ScoreQueryService queries)
{
    public async Task<CommandResult> LeaderboardAsync(ulong guildId, int top = 10, CancellationToken ct = default)
    {
        var board = await queries.GetSeasonLeaderboardAsync(guildId, top, ct);
        return CommandResult.Ok(FormatLeaderboard(board));
    }

    public async Task<CommandResult> WalletAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var wallets = await queries.GetWalletsAsync(guildId, userId, ct);
        return CommandResult.Ok(FormatWallets(wallets));
    }

    // Pure formatters — unit-testable without a database.

    public static string FormatLeaderboard(IReadOnlyList<LeaderboardEntry> board)
    {
        if (board.Count == 0)
        {
            return "No scores yet this season.";
        }

        var lines = board.Select((e, i) => $"{i + 1}. <@{e.UserId}> — **{e.Total}**");
        return "**Season leaderboard**\n" + string.Join("\n", lines);
    }

    public static string FormatWallets(IReadOnlyList<WalletBalance> wallets)
    {
        if (wallets.Count == 0)
        {
            return "You don't have any balances yet.";
        }

        var lines = wallets.Select(w => $"{w.CurrencyName}: **{w.Balance}**");
        return "**Your balances**\n" + string.Join("\n", lines);
    }
}
