using Muster.Domain.Entities.Members;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Membership;

/// <summary>
/// Keeps the local <see cref="GuildChannel"/> roster in sync from the gateway so the web UI can show/pick
/// channels by name without a live Discord call. Only standalone voice and text channels are stored; threads,
/// categories, and forums are skipped (callers pass <c>null</c> kind for those).
///
/// Sync only touches roster fields (name / kind / position / soft-delete) — it never writes tracking config.
/// A channel that disappears keeps its row <b>soft-deleted</b> when it carries config (Mode != Off) so an admin
/// can clean it up; an unconfigured channel is removed outright. A reappearing channel is un-deleted.
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
        channel.DeletedAt = null; // present on the gateway again
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The channel is gone from Discord: drop it if unconfigured, otherwise soft-delete to keep its config.</summary>
    public async Task RemoveAsync(ulong guildId, ulong channelId, DateTimeOffset? at = null, CancellationToken ct = default)
    {
        var channel = await db.FindChannelAsync(guildId, channelId, ct);
        if (channel is null)
        {
            return;
        }

        if (channel.Mode == TrackedChannelMode.Off)
        {
            db.GuildChannels.Remove(channel);
        }
        else
        {
            channel.DeletedAt ??= at ?? DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Replace the full roster for a guild (used on GuildCreate/reconnect). Returns the count present.</summary>
    public async Task<int> SyncAllAsync(ulong guildId, IEnumerable<(ulong ChannelId, string Name, GuildChannelKind Kind, int? Position)> channels, DateTimeOffset? at = null, CancellationToken ct = default)
    {
        var incoming = channels.ToList();
        var existing = await db.ListChannelsAsync(guildId, ct);
        var now = at ?? DateTimeOffset.UtcNow;

        var incomingIds = incoming.Select(c => c.ChannelId).ToHashSet();
        foreach (var gone in existing.Where(c => !incomingIds.Contains(c.ChannelId)))
        {
            if (gone.Mode == TrackedChannelMode.Off)
            {
                db.GuildChannels.Remove(gone); // unconfigured ghost — no reason to keep
            }
            else
            {
                gone.DeletedAt ??= now; // keep config for cleanup
            }
        }

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
            channel.DeletedAt = null;
        }

        await db.SaveChangesAsync(ct);
        return incoming.Count;
    }
}
