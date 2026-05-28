using Muster.Contracts;

namespace Muster.Web.Live;

/// <summary>
/// In-process fan-out for live session changes. The Wolverine handler that drains the session-events queue
/// calls <see cref="Notify"/>; interactive Blazor components subscribe to <see cref="Updated"/>, filter for
/// the session/guild they're showing, and re-fetch + re-render. Singleton: one hub for the whole web host.
/// Subscribers must unsubscribe on dispose to avoid leaking circuits.
/// </summary>
public interface ISessionUpdateNotifier
{
    event Action<SessionAttendanceChanged> Updated;

    void Notify(SessionAttendanceChanged change);
}

public sealed class SessionUpdateNotifier : ISessionUpdateNotifier
{
    public event Action<SessionAttendanceChanged>? Updated;

    public void Notify(SessionAttendanceChanged change) => Updated?.Invoke(change);
}
