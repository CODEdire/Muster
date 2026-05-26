using Muster.Domain.Entities.Guilds;

namespace Muster.Domain.Entities.Members;

/// <summary>A user's membership in a specific guild.</summary>
public class GuildMember
{
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    public string? Nickname { get; set; }

    /// <summary>Snapshot of the member's Discord role ids, synced from the gateway.</summary>
    public List<ulong> RoleIds { get; set; } = [];

    public DateTimeOffset JoinedAt { get; set; }

    public Guild? Guild { get; set; }
    public DiscordUser? User { get; set; }
}
