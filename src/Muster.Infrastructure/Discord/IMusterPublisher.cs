namespace Muster.Infrastructure.Discord;

/// <summary>
/// Abstraction over posting a muster message and seeding its reaction. Keeps the platform (NetCord
/// REST) out of the command layer so muster creation can be unit-tested with a fake publisher.
/// </summary>
public interface IMusterPublisher
{
    /// <summary>Post the prompt to the channel, add the check-in reaction, and return the message id.</summary>
    Task<ulong> PublishAsync(ulong channelId, string prompt, string emoji, CancellationToken ct = default);
}
