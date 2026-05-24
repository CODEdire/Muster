using Microsoft.Extensions.Logging;
using Muster.Contracts;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// Subscribes to ledger events. In v1 this just logs; it's the seam where outbound "Coin" / loot
/// connectors will forward balance changes to external systems (post-v1).
/// </summary>
public class LedgerEntryRecordedHandler(ILogger<LedgerEntryRecordedHandler> logger)
{
    public void Handle(LedgerEntryRecorded message)
    {
        logger.LogInformation(
            "Ledger entry {LedgerEntryId}: guild {GuildId} user {UserId} {Amount} (currency {CurrencyId})",
            message.LedgerEntryId, message.GuildId, message.UserId, message.Amount, message.CurrencyId);
    }
}
