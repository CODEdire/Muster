using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Members;
using Muster.Domain.Enums;
using Muster.Persistence;

namespace Muster.IntegrationTests.TestSupport;

/// <summary>Test helpers for the <see cref="GuildRoleMapping"/> table (role → permission-tier mappings),
/// which replaced the per-tier role-id lists on <c>GuildSettings</c>.</summary>
public static class RoleMappingTestExtensions
{
    /// <summary>Map a Discord role to one or more permission tiers and save. Upserts — OR-ing the tiers onto an
    /// existing row — so mapping the same role more than once mirrors the real toggle behaviour (one row per role).</summary>
    public static async Task MapRoleAsync(this MusterDbContext db, ulong guildId, ulong roleId, GuildRoleTier tiers)
    {
        var existing = await db.GuildRoleMappings.FirstOrDefaultAsync(m => m.GuildId == guildId && m.RoleId == roleId);
        if (existing is null)
        {
            db.GuildRoleMappings.Add(new GuildRoleMapping { GuildId = guildId, RoleId = roleId, Tiers = tiers });
        }
        else
        {
            existing.Tiers |= tiers;
        }

        await db.SaveChangesAsync();
    }
}
