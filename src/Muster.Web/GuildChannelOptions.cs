using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Web;

/// <summary>
/// Channel-picker options sourced from the synced <c>GuildChannel</c> roster (kept current by the bot), so
/// settings pages get channels by name without a live Discord call. Soft-deleted channels are excluded.
/// </summary>
public sealed class GuildChannelOptions(MusterDbContext db)
{
    public record ChannelOption(ulong Id, string Name);

    public Task<IReadOnlyList<ChannelOption>> TextAsync(ulong guildId, CancellationToken ct = default)
        => ForKindAsync(guildId, GuildChannelKind.Text, ct);

    public Task<IReadOnlyList<ChannelOption>> VoiceAsync(ulong guildId, CancellationToken ct = default)
        => ForKindAsync(guildId, GuildChannelKind.Voice, ct);

    private async Task<IReadOnlyList<ChannelOption>> ForKindAsync(ulong guildId, GuildChannelKind kind, CancellationToken ct)
        => (await db.ListChannelsByKindAsync(guildId, kind, ct))
            .Select(c => new ChannelOption(c.ChannelId, c.Name))
            .ToList();
}
