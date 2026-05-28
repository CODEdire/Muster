using Muster.Contracts;

namespace Muster.Web.Live;

/// <summary>
/// Drains the quest-views SQL queue (web-only listener — a copy of every quest lifecycle event; the bot consumes
/// the parallel quest-board copy) and fans each change out to connected Blazor circuits via the in-process
/// <see cref="IQuestUpdateNotifier"/>. Discovered by Wolverine's handler scan.
/// </summary>
public class QuestLifecycleNotifiedHandler
{
    public void Handle(QuestLifecycleNotified message, IQuestUpdateNotifier notifier)
        => notifier.Notify(message);
}
