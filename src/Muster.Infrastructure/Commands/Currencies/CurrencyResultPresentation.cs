using Muster.Contracts;

namespace Muster.Infrastructure.Commands.Currencies;

/// <summary>Maps a currency command-handler <see cref="Result"/> to user-facing text. Stable status codes
/// (CurrencyNotFound / InsufficientFunds / Forbidden) get friendly copy; validation messages pass through verbatim.</summary>
public static class CurrencyResultPresentation
{
    public static CommandResult ToCurrencyResult(this Result result, string okMessage)
        => result.Ok
            ? CommandResult.Ok(okMessage)
            : CommandResult.Error(result.Status switch
            {
                "CurrencyNotFound" => "That currency doesn't exist in this server.",
                "InsufficientFunds" => "Not enough balance for that.",
                "Forbidden" => "You're not allowed to do that.",
                var other => other, // validation copy from the handler
            });
}
