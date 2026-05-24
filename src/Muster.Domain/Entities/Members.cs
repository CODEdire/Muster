namespace Muster.Domain.Entities;

/// <summary>A Discord user, shared across guilds.</summary>
public class DiscordUser
{
    /// <summary>Discord user snowflake.</summary>
    public ulong Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? GlobalName { get; set; }
    public string? AvatarHash { get; set; }
}

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
