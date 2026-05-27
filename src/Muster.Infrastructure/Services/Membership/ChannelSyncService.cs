using Muster.Domain.Entities.Members;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Membership;

/// <summary>
/// Keeps the local <see cref="GuildChannel"/> snapshot in sync so the web UI can offer channel pickers by
/// name without a live Discord call. Only standalone voice and text channels are stored; threads, categories,
/// and forums are skipped (callers pass <c>null</c> kind for those).
/// </summary>
public class ChannelSyncService(MusterDbContext db)
{
    public async Task UpsertAsync(ulong guildId, ulong channelId, string name, GuildChannelKind kind, int? position, CancellationToken ct = default)
    {
        var channel = await db.FindChannelAsync(guildId, channelId, ct);
        if (channel is null)
        {
            channel = new GuildChannel { GuildId = guildId, ChannelId = channelId };
            db.GuildChannels.Add(channel);
        }

        channel.Name = name;
        channel.Kind = kind;
        channel.Position = position;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(ulong guildId, ulong channelId, CancellationToken ct = default)
    {
        var channel = await db.FindChannelAsync(guildId, channelId, ct);
        if (channel is not null)
        {
            db.GuildChannels.Remove(channel);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Replace the full channel set for a guild (used on GuildCreate). Returns the count kept.</summary>
    public async Task<int> SyncAllAsync(ulong guildId, IEnumerable<(ulong ChannelId, string Name, GuildChannelKind Kind, int? Position)> channels, CancellationToken ct = default)
    {
        var incoming = channels.ToList();
        var existing = await db.ListChannelsAsync(guildId, ct);

        var incomingIds = incoming.Select(c => c.ChannelId).ToHashSet();
        db.GuildChannels.RemoveRange(existing.Where(c => !incomingIds.Contains(c.ChannelId)));

        foreach (var (channelId, name, kind, position) in incoming)
        {
            var channel = existing.FirstOrDefault(c => c.ChannelId == channelId);
            if (channel is null)
            {
                channel = new GuildChannel { GuildId = guildId, ChannelId = channelId };
                db.GuildChannels.Add(channel);
            }

            channel.Name = name;
            channel.Kind = kind;
            channel.Position = position;
        }

        await db.SaveChangesAsync(ct);
        return incoming.Count;
    }
}
