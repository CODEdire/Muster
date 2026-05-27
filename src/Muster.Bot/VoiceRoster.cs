using NetCord.Gateway;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Bot;

/// <summary>Builds the current voice roster (grouped by channel) from the gateway cache for background reconcile.</summary>
public static class VoiceRoster
{
    public static IReadOnlyDictionary<ulong, IReadOnlyList<VoiceMemberSnapshot>> Snapshot(GatewayClient client, ulong guildId)
    {
        var byChannel = new Dictionary<ulong, List<VoiceMemberSnapshot>>();

        if (client.Cache.Guilds.TryGetValue(guildId, out var guild))
        {
            foreach (var vs in guild.VoiceStates.Values)
            {
                if (vs.ChannelId is not { } channelId)
                {
                    continue;
                }

                var muted = vs.IsMuted || vs.IsSelfMuted;
                var deafened = vs.IsDeafened || vs.IsSelfDeafened;
                var isBot = vs.User?.IsBot ?? false;

                if (!byChannel.TryGetValue(channelId, out var list))
                {
                    list = [];
                    byChannel[channelId] = list;
                }

                list.Add(new VoiceMemberSnapshot(vs.UserId, isBot, muted, deafened));
            }
        }

        return byChannel.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<VoiceMemberSnapshot>)kv.Value);
    }
}
