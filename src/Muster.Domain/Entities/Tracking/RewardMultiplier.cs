using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Tracking;

/// <summary>
/// A per-guild rule that scales participation rewards by <see cref="Factor"/> when it's active. Three kinds:
/// a one-off absolute window, a weekly recurring window (evaluated in the guild's time zone), or an always-on
/// per-role factor. Which reward planes it touches is set by <see cref="Scope"/>. The effective factor at any
/// moment is combined across active rules per the guild's <see cref="GuildSettings.MultiplierStacking"/> and
/// clamped by <see cref="GuildSettings.MultiplierCap"/>.
/// </summary>
public class RewardMultiplier
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Human label for the admin UI (e.g. "Friday raid 2×", "Weeknight peak").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The scaling factor (e.g. 1.5, 2.0; values below 1 de-emphasise off-peak time). Must be &gt; 0.</summary>
    public decimal Factor { get; set; } = 1m;

    public MultiplierKind Kind { get; set; }

    /// <summary>Reward planes this applies to.</summary>
    public MultiplierScope Scope { get; set; } = MultiplierScope.All;

    public bool Enabled { get; set; } = true;

    // --- OneOff ---
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }

    // --- Recurring (guild-local time of day; window may wrap past midnight) ---
    public WeekDays Days { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    // --- Role ---
    public ulong RoleId { get; set; }
}
