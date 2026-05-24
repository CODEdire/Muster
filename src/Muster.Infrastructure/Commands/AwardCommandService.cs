using Muster.Infrastructure.Services;

namespace Muster.Infrastructure.Commands;

/// <summary>Platform-independent logic for the "award points" command. Fully unit-testable.</summary>
public class AwardCommandService(ManualAwardService awards)
{
    public async Task<CommandResult> AwardPointsAsync(
        ulong guildId, ulong actorId, ulong memberId, long amount, string reason, CancellationToken ct = default)
    {
        if (amount <= 0)
        {
            return CommandResult.Error("Amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return CommandResult.Error("Please provide a reason for the award.");
        }

        await awards.AwardPointsAsync(guildId, memberId, amount, reason.Trim(), actorId, ct);

        return CommandResult.Ok($"Awarded **{amount}** points to <@{memberId}> — {reason.Trim()}");
    }

    /// <summary>Award a chosen currency (by code) to a member.</summary>
    public async Task<CommandResult> AwardCurrencyAsync(
        ulong guildId, ulong actorId, ulong memberId, string currencyCode, long amount, string reason, CancellationToken ct = default)
    {
        if (amount <= 0)
        {
            return CommandResult.Error("Amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return CommandResult.Error("Please provide a reason for the award.");
        }

        var ok = await awards.AwardByCodeAsync(guildId, memberId, currencyCode, amount, reason.Trim(), actorId, ct);
        return ok
            ? CommandResult.Ok($"Awarded **{amount} {currencyCode.Trim().ToUpperInvariant()}** to <@{memberId}> — {reason.Trim()}")
            : CommandResult.Error($"Unknown currency '{currencyCode}'.");
    }
}
