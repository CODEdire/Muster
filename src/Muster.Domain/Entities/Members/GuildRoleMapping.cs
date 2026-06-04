using Muster.Domain.Entities.Guilds;
using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Members;

/// <summary>
/// Maps a Discord role to the app permission tiers it grants in one guild. One row per (guild, role);
/// <see cref="Tiers"/> is the bitmask of granted tiers (see <see cref="GuildRoleTier"/>). Replaces the
/// per-tier role-id lists that used to live in <see cref="GuildSettings"/> as a JSON document — a real
/// table so toggles are single-row writes (no read-modify-write of the whole settings blob), roles can be
/// joined/queried, and a deleted guild cascades its mappings away. A row whose <see cref="Tiers"/> falls to
/// <see cref="GuildRoleTier.None"/> carries no grant and is deleted rather than kept.
/// </summary>
public class GuildRoleMapping
{
    public ulong GuildId { get; set; }

    /// <summary>Discord role snowflake. Not FK'd to <see cref="GuildRole"/> — a mapping may outlive a
    /// temporarily-unsynced role snapshot.</summary>
    public ulong RoleId { get; set; }

    /// <summary>The bitmask of permission tiers this role grants.</summary>
    public GuildRoleTier Tiers { get; set; }

    public Guild? Guild { get; set; }
}
