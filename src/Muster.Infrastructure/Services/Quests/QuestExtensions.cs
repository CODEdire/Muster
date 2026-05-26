using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Wolverine;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>Quest-flavoured extensions: result mapping and lifecycle-event publishing.</summary>
public static class QuestExtensions
{
    /// <summary>Map an escrow outcome onto the quest result a caller sees.</summary>
    public static QuestResult ToQuestResult(this EscrowStatus status) => status switch
    {
        EscrowStatus.Ok => QuestResult.Ok,
        EscrowStatus.InsufficientFunds => QuestResult.InsufficientFunds,
        EscrowStatus.NotSpendable => QuestResult.NotSpendable,
        EscrowStatus.CurrencyNotFound => QuestResult.NotFound,
        _ => QuestResult.InvalidState,
    };

    /// <summary>Publish a quest lifecycle moment. <paramref name="target"/> is who should hear about it (null = managers).</summary>
    public static ValueTask NotifyAsync(this IMessageBus bus, GuildQuest quest, QuestLifecycleMoment moment, ulong? target, string detail)
        => bus.PublishAsync(new QuestLifecycleNotified(quest.GuildId, quest.Id, quest.Name, moment, target, detail));

    /// <summary>Publish the post-creation moment, derived from the quest's origin and intake status.</summary>
    public static ValueTask NotifyCreatedAsync(this IMessageBus bus, GuildQuest quest)
    {
        var (moment, target, detail) = quest switch
        {
            { Origin: QuestOrigin.Player, Status: QuestStatus.PendingApproval }
                => (QuestLifecycleMoment.PendingApproval, (ulong?)null, "Awaiting intake approval."),
            { Origin: QuestOrigin.Player }
                => (QuestLifecycleMoment.Created, quest.OwnerId, "Personal quest posted."),
            _ => (QuestLifecycleMoment.Created, (ulong?)null, "Quest created."),
        };

        return bus.NotifyAsync(quest, moment, target, detail);
    }
}
