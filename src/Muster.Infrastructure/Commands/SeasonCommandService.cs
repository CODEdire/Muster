
using Muster.Infrastructure.Services.Seasons;
namespace Muster.Infrastructure.Commands;

/// <summary>Platform-independent logic for season management commands.</summary>
public class SeasonCommandService(SeasonService seasons)
{
    public async Task<CommandResult> StartAsync(ulong guildId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult.Error("Please provide a season name.");
        }

        var season = await seasons.StartAsync(guildId, name.Trim(), ct);
        return CommandResult.Ok($"Started season **{season.Name}**. The previous season (if any) was archived.");
    }

    public async Task<CommandResult> EndAsync(ulong guildId, CancellationToken ct = default)
    {
        var ended = await seasons.EndAsync(guildId, ct);
        return ended is null
            ? CommandResult.Error("There's no active season to end.")
            : CommandResult.Ok($"Ended season **{ended.Name}**.");
    }

    public async Task<CommandResult> StatusAsync(ulong guildId, CancellationToken ct = default)
    {
        var active = await seasons.GetActiveAsync(guildId, ct);
        return active is null
            ? CommandResult.Ok("No active season.")
            : CommandResult.Ok($"Active season: **{active.Name}** (started {active.StartsAt:yyyy-MM-dd}).");
    }
}
