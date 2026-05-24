using Muster.Infrastructure.Discord;
using Muster.Infrastructure.Services;

namespace Muster.Infrastructure.Commands;

/// <summary>
/// Logic for creating a reaction muster. Posting the Discord message + reaction is delegated to
/// <see cref="IMusterPublisher"/>, so this is unit-testable with a fake publisher.
/// </summary>
public class MusterCommandService(MusterService musters, IMusterPublisher publisher)
{
    public async Task<CommandResult> CreateAsync(
        ulong guildId, ulong channelId, string prompt, string emoji, long reward, int? capacity,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return CommandResult.Error("Please provide a prompt for the muster.");
        }

        if (string.IsNullOrWhiteSpace(emoji))
        {
            return CommandResult.Error("Please provide an emoji for members to react with.");
        }

        if (reward < 0)
        {
            return CommandResult.Error("Reward can't be negative.");
        }

        if (capacity is <= 0)
        {
            return CommandResult.Error("Capacity must be a positive number.");
        }

        var messageId = await publisher.PublishAsync(channelId, prompt.Trim(), emoji, ct);
        await musters.CreatePointsAsync(guildId, channelId, messageId, prompt.Trim(), [emoji], reward, capacity, expiresAt: null, ct);

        return CommandResult.Ok($"Posted a muster in <#{channelId}>. React with {emoji} to check in for **{reward}** points.");
    }
}
