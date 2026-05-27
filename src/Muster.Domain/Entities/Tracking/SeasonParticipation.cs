namespace Muster.Domain.Entities.Tracking;

/// <summary>
/// Per-(guild, user, season) participation-time accumulator. Active voice minutes are added as they
/// accrue (attributed to whatever season was open at the time), so seasonal "voice hours" totals are
/// exact across a season rollover with no boundary recalculation. Stats only — independent of currency.
/// </summary>
public class SeasonParticipation
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public Guid SeasonId { get; set; }

    /// <summary>Cumulative active voice minutes this member accrued during the season.</summary>
    public int VoiceMinutes { get; set; }
}
