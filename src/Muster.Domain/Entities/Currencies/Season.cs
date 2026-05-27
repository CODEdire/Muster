using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Currencies;

/// <summary>A scoring period. Points leaderboards are scoped to a season; spendable currencies persist.</summary>
public class Season
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public SeasonStatus Status { get; set; } = SeasonStatus.Active;
}
