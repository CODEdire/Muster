namespace Muster.Contracts;

// Tracking / live-session message contracts. The seam a broader notification framework builds on.

/// <summary>
/// Published whenever a tracking session's live attendance changes in a way a watcher would notice — a
/// member joins, leaves, pauses/resumes, opts out, or the session closes. Carries no payload beyond
/// identity + reason: consumers re-fetch the read model (the source of truth) and re-render, so a missed
/// or duplicated message only costs a redundant refresh. Routed over a durable SQL queue because the
/// mutation usually happens in the <b>bot</b> process (voice reconcile) while the live web view runs in
/// the <b>web</b> process; only the web listens and fans out to connected Blazor circuits.
/// </summary>
public record SessionAttendanceChanged(ulong GuildId, Guid SessionId, SessionChangeKind Kind);

/// <summary>Why a <see cref="SessionAttendanceChanged"/> fired. Lets future consumers (toasts, sounds) react
/// differently per reason; today the live views treat them all as "re-fetch".</summary>
public enum SessionChangeKind
{
    /// <summary>A member joined, left, paused, or resumed during the voice reconcile.</summary>
    Roster,

    /// <summary>A member opted out of this one session.</summary>
    OptOut,

    /// <summary>The session ended.</summary>
    Closed,

    /// <summary>A new session was opened (so live session lists pick it up immediately).</summary>
    Opened,
}
