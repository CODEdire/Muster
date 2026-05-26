using Muster.Contracts;
using Muster.Infrastructure.Services.Ledger;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// CQRS command handlers for currency mint/spend. The public API invokes these through the Wolverine
/// bus, so they run inside the transactional/outbox pipeline. They delegate to <see cref="ICurrencyService"/>
/// for the mode-aware overdraft logic and are unit-testable by calling the static methods directly.
/// </summary>
public static class MintCurrencyHandler
{
    public static async Task<CurrencyChangeResult> Handle(MintCurrency command, ICurrencyService currency, CancellationToken ct)
    {
        var result = await currency.MintAsync(command.GuildId, command.CurrencyCode, command.UserId, command.Amount, command.Reason, ct);
        return ToResult(result);
    }

    internal static CurrencyChangeResult ToResult(CurrencyOperationResult result)
        => new(result.Status == CurrencyOperationStatus.Ok, result.Status.ToString(), result.Balance);
}

public static class SpendCurrencyHandler
{
    public static async Task<CurrencyChangeResult> Handle(SpendCurrency command, ICurrencyService currency, CancellationToken ct)
    {
        var result = await currency.SpendAsync(command.GuildId, command.CurrencyCode, command.UserId, command.Amount, command.Reason, ct);
        return MintCurrencyHandler.ToResult(result);
    }
}
