namespace Muster.Domain.Entities.Members;

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

    /// <summary>When true, the user has opted out of DM receipts for currency they receive (transfers in, staff
    /// mints/adjustments). Default false = receipts on. Toggled via <c>/currency notify</c> or the web wallet.</summary>
    public bool CurrencyDmOptOut { get; set; }
}
