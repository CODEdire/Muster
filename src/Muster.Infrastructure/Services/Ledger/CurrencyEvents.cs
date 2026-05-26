using Microsoft.Extensions.Logging;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Ledger;

/// <summary>
/// A single ledger movement (one <c>LedgerEntry</c>), emitted by <see cref="CurrencyService"/> the moment
/// it's staged. <paramref name="Amount"/> is signed (negative = debit), so consumers see the raw stream —
/// credits, debits, escrow legs (including the house account, see <see cref="CurrencyService.EscrowAccountUserId"/>),
/// the lot — and filter for what they care about. The external rewards/loot connector subscribes here.
/// </summary>
public record CurrencyMovement(
    ulong GuildId,
    ulong UserId,
    Guid CurrencyId,
    long Amount,
    LedgerSourceType SourceType,
    string? SourceId,
    string Reason,
    Guid? SeasonId);

/// <summary>
/// Seam for currency-movement propagation. <see cref="CurrencyService"/> is the single source of truth for
/// money, so it owns this emission; the default just logs. A durable implementation lands later via the
/// transactional outbox (the publish staged in the same transaction as the ledger write) — and the external
/// connector that resolves rewards beyond Muster's own ledger consumes it from there.
/// </summary>
public interface ICurrencyEventSink
{
    Task OnMovementAsync(CurrencyMovement movement, CancellationToken ct = default);
}

/// <summary>No-op sink — safe default for tests and contexts with no propagation wired.</summary>
public sealed class NullCurrencyEventSink : ICurrencyEventSink
{
    public Task OnMovementAsync(CurrencyMovement movement, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Logs every movement until the durable outbox + external connector are wired up.</summary>
public class LoggingCurrencyEventSink(ILogger<LoggingCurrencyEventSink> logger) : ICurrencyEventSink
{
    public Task OnMovementAsync(CurrencyMovement movement, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Currency movement: guild {GuildId} user {User} {Amount} of {Currency} via {Source} ({SourceId}): {Reason}",
            movement.GuildId, movement.UserId, movement.Amount, movement.CurrencyId,
            movement.SourceType, movement.SourceId ?? "-", movement.Reason);
        return Task.CompletedTask;
    }
}
