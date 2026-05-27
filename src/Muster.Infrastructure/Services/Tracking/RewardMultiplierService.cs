using Muster.Domain.Entities.Tracking;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>
/// Loads a guild's reward multipliers once (DB hit) into an immutable <see cref="MultiplierSet"/> that the
/// reward sinks then query per member/flush with no further IO. Splitting load from evaluation keeps the
/// hot path (background reconcile credits many members per pass) cheap.
/// </summary>
public sealed class RewardMultiplierService(MusterDbContext db)
{
    public async Task<MultiplierSet> LoadAsync(ulong guildId, CancellationToken ct = default)
    {
        var multipliers = await db.ListEnabledMultipliersAsync(guildId, ct);
        var settings = await db.GetSettingsAsync(guildId, ct);
        var zoneId = await db.GuildTimeZoneIdAsync(guildId, ct);
        return new MultiplierSet(multipliers, settings.MultiplierStacking, settings.MultiplierCap, ResolveZone(zoneId));
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Utc;
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}

/// <summary>
/// An immutable snapshot of a guild's enabled multipliers + stacking policy. <see cref="Factor"/> is a pure
/// function of (plane, instant, member roles): time-window factors combine per the guild's stacking mode, the
/// member's highest matching role factor multiplies that, and the result is clamped by the guild cap.
/// </summary>
public sealed class MultiplierSet
{
    private readonly IReadOnlyList<RewardMultiplier> _multipliers;
    private readonly MultiplierStacking _stacking;
    private readonly decimal _cap;
    private readonly TimeZoneInfo _zone;

    public MultiplierSet(IReadOnlyList<RewardMultiplier> multipliers, MultiplierStacking stacking, decimal cap, TimeZoneInfo zone)
    {
        _multipliers = multipliers;
        _stacking = stacking;
        _cap = cap;
        _zone = zone;
    }

    /// <summary>True when there are no multipliers at all — callers can skip the factor math entirely.</summary>
    public bool IsEmpty => _multipliers.Count == 0;

    /// <summary>
    /// The effective factor for the given reward plane at <paramref name="at"/> (UTC), optionally including the
    /// member's role multipliers. Returns 1.0 when nothing applies.
    /// </summary>
    public decimal Factor(MultiplierScope plane, DateTimeOffset at, IReadOnlyCollection<ulong>? roleIds = null)
    {
        if (_multipliers.Count == 0)
        {
            return 1m;
        }

        decimal time = 1m;
        var anyWindow = false;
        decimal roleMax = 0m;

        foreach (var m in _multipliers)
        {
            if (m.Factor <= 0m || (m.Scope & plane) == 0)
            {
                continue;
            }

            if (m.Kind == MultiplierKind.Role)
            {
                if (roleIds is not null && roleIds.Contains(m.RoleId) && m.Factor > roleMax)
                {
                    roleMax = m.Factor;
                }

                continue;
            }

            if (!WindowActive(m, at))
            {
                continue;
            }

            if (_stacking == MultiplierStacking.Multiplicative)
            {
                time = anyWindow ? time * m.Factor : m.Factor;
            }
            else if (!anyWindow || m.Factor > time)
            {
                time = m.Factor; // HighestWins
            }

            anyWindow = true;
        }

        var roleFactor = roleMax > 0m ? roleMax : 1m;
        var effective = time * roleFactor;

        if (_cap > 0m && effective > _cap)
        {
            effective = _cap;
        }

        return effective;
    }

    private bool WindowActive(RewardMultiplier m, DateTimeOffset at)
    {
        if (m.Kind == MultiplierKind.OneOff)
        {
            return (m.StartsAt is null || at >= m.StartsAt) && (m.EndsAt is null || at < m.EndsAt);
        }

        // Recurring: evaluate in the guild's local wall clock.
        if (m.StartTime is not { } start || m.EndTime is not { } end || m.Days == WeekDays.None)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(at, _zone);
        var tod = TimeOnly.FromDateTime(local.DateTime);
        var today = ToFlag(local.DayOfWeek);

        if (start < end)
        {
            return Has(m.Days, today) && tod >= start && tod < end;
        }

        if (start > end)
        {
            // Window wraps past midnight: late part of a selected day, or early part of the following day.
            var yesterday = ToFlag(local.AddDays(-1).DayOfWeek);
            return (Has(m.Days, today) && tod >= start) || (Has(m.Days, yesterday) && tod < end);
        }

        // start == end: treat as all day on the selected days.
        return Has(m.Days, today);
    }

    private static bool Has(WeekDays set, WeekDays day) => (set & day) != 0;

    private static WeekDays ToFlag(DayOfWeek d) => (WeekDays)(1 << (int)d);
}
