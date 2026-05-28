using Muster.Contracts;

namespace Muster.Web.Live;

/// <summary>
/// Drains the session-events SQL queue (web-only listener) and fans each change out to connected Blazor
/// circuits via the in-process <see cref="ISessionUpdateNotifier"/>. Discovered by Wolverine's handler scan.
/// </summary>
public class SessionAttendanceChangedHandler
{
    public void Handle(SessionAttendanceChanged message, ISessionUpdateNotifier notifier)
        => notifier.Notify(message);
}
