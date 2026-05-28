using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Tracking;

/// <summary>
/// A single presence transition for a session attendee (join / resume / pause / leave), timestamped at the
/// reconcile pass that observed it. The ordered stream per member reconstructs the exact attendance timeline —
/// presence runs (join→leave) and earning runs (resume→pause/leave) — for the live-ops charts and audit log.
/// Resolution is the reconcile cadence, not millisecond-accurate Discord events.
/// </summary>
public class SessionPresenceEvent
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public ulong UserId { get; set; }
    public SessionPresenceKind Kind { get; set; }

    /// <summary>For a <see cref="SessionPresenceKind.Pause"/>, why earning stopped (e.g. "deafened", "alone",
    /// "muted, alone") — the tripped anti-AFK guard(s). Null for the other kinds.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset AtUtc { get; set; }
}
