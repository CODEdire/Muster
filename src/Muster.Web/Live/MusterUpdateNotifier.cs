using Muster.Contracts;

namespace Muster.Web.Live;

/// <summary>
/// In-process fan-out for live muster changes. The Wolverine handler that drains the muster-events queue calls
/// <see cref="Notify"/>; interactive Blazor components subscribe to <see cref="Updated"/>, filter for the muster
/// they're showing, and re-fetch + re-render. Singleton: one hub for the whole web host. Subscribers must
/// unsubscribe on dispose to avoid leaking circuits. Mirrors <see cref="ISessionUpdateNotifier"/>.
/// </summary>
public interface IMusterUpdateNotifier
{
    event Action<MusterChanged> Updated;

    void Notify(MusterChanged change);
}

public sealed class MusterUpdateNotifier : IMusterUpdateNotifier
{
    public event Action<MusterChanged>? Updated;

    public void Notify(MusterChanged change) => Updated?.Invoke(change);
}
