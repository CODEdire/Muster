
using Muster.Infrastructure.Services.Seasons;
namespace Muster.Infrastructure.Commands.Seasons;

/// <summary>Identifying info for a season transition — what the UI needs to audit a start/end without a re-read.</summary>
public record SeasonInfo(Guid Id, string Name, DateTimeOffset OccurredAt);

/// <summary>Platform-independent logic for season management commands.</summary>
public class SeasonCommandService(SeasonService seasons)
{
    public async Task<CommandResult<SeasonInfo>> StartAsync(ulong guildId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CommandResult<SeasonInfo>.Error("Please provide a season name.");
        }

        var season = await seasons.StartAsync(guildId, name.Trim(), ct);
        return CommandResult<SeasonInfo>.Ok(
            new SeasonInfo(season.Id, season.Name, season.StartsAt),
            $"Started season **{season.Name}**. The previous season (if any) was archived.");
    }

    public async Task<CommandResult<SeasonInfo>> EndAsync(ulong guildId, CancellationToken ct = default)
    {
        var ended = await seasons.EndAsync(guildId, ct);
        return ended is null
            ? CommandResult<SeasonInfo>.Error("There's no active season to end.")
            : CommandResult<SeasonInfo>.Ok(
                new SeasonInfo(ended.Id, ended.Name, ended.EndsAt ?? DateTimeOffset.UtcNow),
                $"Ended season **{ended.Name}**.");
    }

    public async Task<CommandResult> StatusAsync(ulong guildId, CancellationToken ct = default)
    {
        var active = await seasons.GetActiveAsync(guildId, ct);
        return active is null
            ? CommandResult.Ok("No active season.")
            : CommandResult.Ok($"Active season: **{active.Name}** (started {active.StartsAt:yyyy-MM-dd}).");
    }
}
