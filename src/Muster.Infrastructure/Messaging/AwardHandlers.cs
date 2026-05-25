using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Ledger;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Wolverine handlers for the broker-agnostic award contracts. Each awards through
/// <see cref="AwardService"/> and returns a <see cref="LedgerEntryRecorded"/> as a cascading message,
/// which Wolverine publishes through the durable outbox — the hook outbound connectors subscribe to.
///
/// These are plain methods: Wolverine injects the services at runtime, and unit tests call them directly.
/// </summary>
public static class AwardCurrencyHandler
{
    public static async Task<LedgerEntryRecorded> Handle(AwardCurrency command, AwardService awards, CancellationToken ct)
    {
        var sourceType = Enum.Parse<LedgerSourceType>(command.SourceType, ignoreCase: true);
        var entry = await awards.AwardAsync(
            command.GuildId, command.UserId, command.CurrencyId, command.Amount,
            sourceType, command.SourceId, command.Reason, ct);

        return ToEvent(entry);
    }

    internal static LedgerEntryRecorded ToEvent(Muster.Domain.Entities.LedgerEntry e)
        => new(e.Id, e.GuildId, e.UserId, e.CurrencyId, e.Amount, e.OccurredAt);
}

public static class MemberParticipatedHandler
{
    public static async Task<LedgerEntryRecorded> Handle(MemberParticipated message, AwardService awards, CancellationToken ct)
    {
        var sourceType = Enum.Parse<LedgerSourceType>(message.SourceType, ignoreCase: true);
        var entry = await awards.AwardPointsAsync(
            message.GuildId, message.UserId, message.SuggestedAmount,
            sourceType, message.SourceId, message.Reason, ct);

        return AwardCurrencyHandler.ToEvent(entry);
    }
}

/// <summary>Connector-driven mint/spend of a spendable currency.</summary>
public static class AdjustCurrencyBalanceHandler
{
    public static async Task<LedgerEntryRecorded> Handle(AdjustCurrencyBalance command, AwardService awards, CancellationToken ct)
    {
        var entry = await awards.AwardAsync(
            command.GuildId, command.UserId, command.CurrencyId, command.Delta,
            LedgerSourceType.Connector, sourceId: null, command.Reason, ct);

        return AwardCurrencyHandler.ToEvent(entry);
    }
}
