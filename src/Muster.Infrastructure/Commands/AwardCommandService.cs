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
}
