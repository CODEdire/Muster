using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Members;

/// <summary>
/// A snapshot of a Discord guild channel (voice or text), kept in sync from the gateway so the web UI can
/// offer channel pickers by name without a live Discord call. Only standalone voice and text channels are
/// stored — threads, categories, and forums are skipped.
/// </summary>
public class GuildChannel
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public string Name { get; set; } = string.Empty;
    public GuildChannelKind Kind { get; set; }

    /// <summary>Discord sort position within the guild (for ordering pickers); null when unknown.</summary>
    public int? Position { get; set; }
}
