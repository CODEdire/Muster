namespace Muster.Contracts;

// Broker-agnostic Wolverine message contracts (split by feature: CurrencyMessages.cs, QuestMessages.cs). In v1
// these flow in-process per container against the shared database + durable outbox. Enabling the Azure Service Bus
// transport later turns the bot -> web publishes into real cross-container delivery with no handler changes.

/// <summary>
/// A guild-scoped command carrying the acting member. The audit middleware reads these to record every
/// successful command uniformly (action = command type name), so adapters don't each re-implement auditing.
/// </summary>
public interface IGuildCommand
{
    ulong GuildId { get; }
    ulong ActorId { get; }
}
