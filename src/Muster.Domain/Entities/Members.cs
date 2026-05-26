namespace Muster.Domain.Entities;

/// <summary>A Discord user, shared across guilds.</summary>
public class DiscordUser
{
    /// <summary>Discord user snowflake.</summary>
    public ulong Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? GlobalName { get; set; }
    public string? AvatarHash { get; set; }

    /// <summary>True for a Discord bot/app account. Synced from the gateway. Bots are hidden from human-facing
    /// lists (leaderboard, award picker) but may be bound as an API key's service actor.</summary>
    public bool IsBot { get; set; }

    /// <summary>Preferred IANA time zone (e.g. "America/New_York") for interpreting dates the user enters.
    /// Null falls back to the guild's time zone, then UTC.</summary>
    public string? TimeZoneId { get; set; }
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

/// <summary>A Discord role snapshot, used to resolve permissions (Administrator / Manage Guild).</summary>
public class GuildRole
{
    public ulong GuildId { get; set; }
    public ulong RoleId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Raw Discord permission bitfield for the role.</summary>
    public ulong Permissions { get; set; }
}
