using Muster.Infrastructure.Discord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Muster.Bot;

/// <summary>NetCord-backed <see cref="IMusterPublisher"/>: posts the muster message and seeds its reaction.</summary>
public class NetCordMusterPublisher(GatewayClient client) : IMusterPublisher
{
    public async Task<ulong> PublishAsync(ulong channelId, string prompt, string emoji, CancellationToken ct = default)
    {
        var message = await client.Rest.SendMessageAsync(
            channelId, new MessageProperties { Content = prompt }, cancellationToken: ct);

        await client.Rest.AddMessageReactionAsync(channelId, message.Id, emoji, cancellationToken: ct);

        return message.Id;
    }
}
