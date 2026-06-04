using Muster.Contracts;

namespace Muster.Web.Live;

/// <summary>
/// Drains the muster-events queue (web subscriber) and fans each change out to connected Blazor circuits via the
/// in-process <see cref="IMusterUpdateNotifier"/> — so the detail/board update live on a check-in from Discord, web,
/// or API. Discovered by Wolverine's handler scan.
/// </summary>
public class MusterChangedHandler
{
    public void Handle(MusterChanged message, IMusterUpdateNotifier notifier)
        => notifier.Notify(message);
}
