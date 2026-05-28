using Muster.Contracts;

namespace Muster.Web.Live;

/// <summary>
/// In-process fan-out for live quest changes. The Wolverine handler that drains the quest-views queue calls
/// <see cref="Notify"/>; interactive Blazor components subscribe to <see cref="Updated"/>, filter for the
/// guild/quest they're showing, and re-fetch + re-render. Singleton: one hub for the whole web host.
/// Subscribers must unsubscribe on dispose to avoid leaking circuits. Mirrors <see cref="ISessionUpdateNotifier"/>.
/// </summary>
public interface IQuestUpdateNotifier
{
    event Action<QuestLifecycleNotified> Updated;

    void Notify(QuestLifecycleNotified change);
}

public sealed class QuestUpdateNotifier : IQuestUpdateNotifier
{
    public event Action<QuestLifecycleNotified>? Updated;

    public void Notify(QuestLifecycleNotified change) => Updated?.Invoke(change);
}
