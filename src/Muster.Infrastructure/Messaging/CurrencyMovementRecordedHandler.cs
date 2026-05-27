using Microsoft.Extensions.Logging;
using Muster.Contracts;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Logs every staged ledger movement — the observability seam and transactional-outbox successor to the old
/// in-process <c>ICurrencyEventSink</c>. <c>CurrencyService</c> publishes <see cref="CurrencyMovementRecorded"/>
/// on <i>every</i> path (award/mint/spend/transfer/adjust/escrow); the external rewards connector will consume
/// this seam later. Connector delivery itself is synchronous in <c>CurrencyService.StageAsync</c>, so this is
/// audit/observability only.
/// </summary>
public class CurrencyMovementRecordedHandler(ILogger<CurrencyMovementRecordedHandler> logger)
{
    public void Handle(CurrencyMovementRecorded m) =>
        logger.LogDebug("Currency movement: guild {Guild} user {User} {Amount} of {Currency} via {Source} ({SourceId}): {Reason}",
            m.GuildId, m.UserId, m.Amount, m.CurrencyId, m.SourceType, m.SourceId ?? "-", m.Reason);
}
