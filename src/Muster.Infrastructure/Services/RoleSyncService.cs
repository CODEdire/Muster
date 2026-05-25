using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;

namespace Muster.Infrastructure.Services;

/// <summary>
/// Keeps the local <see cref="GuildRole"/> snapshot in sync so the admin bypass can tell which roles
/// carry Administrator / Manage Guild permissions.
/// </summary>
public class RoleSyncService(MusterDbContext db)
{
    public async Task UpsertAsync(ulong guildId, ulong roleId, string name, ulong permissions, CancellationToken ct = default)
    {
        var role = await db.GuildRoles.FirstOrDefaultAsync(r => r.GuildId == guildId && r.RoleId == roleId, ct);
        if (role is null)
        {
            role = new GuildRole { GuildId = guildId, RoleId = roleId };
            db.GuildRoles.Add(role);
        }

        role.Name = name;
        role.Permissions = permissions;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(ulong guildId, ulong roleId, CancellationToken ct = default)
    {
        var role = await db.GuildRoles.FirstOrDefaultAsync(r => r.GuildId == guildId && r.RoleId == roleId, ct);
        if (role is not null)
        {
            db.GuildRoles.Remove(role);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Replace the full role set for a guild (used on GuildCreate).</summary>
    public async Task SyncAllAsync(ulong guildId, IEnumerable<(ulong RoleId, string Name, ulong Permissions)> roles, CancellationToken ct = default)
    {
        var incoming = roles.ToList();
        var existing = await db.GuildRoles.Where(r => r.GuildId == guildId).ToListAsync(ct);

        var incomingIds = incoming.Select(r => r.RoleId).ToHashSet();
        db.GuildRoles.RemoveRange(existing.Where(r => !incomingIds.Contains(r.RoleId)));

        foreach (var (roleId, name, permissions) in incoming)
        {
            var role = existing.FirstOrDefault(r => r.RoleId == roleId);
            if (role is null)
            {
                role = new GuildRole { GuildId = guildId, RoleId = roleId };
                db.GuildRoles.Add(role);
            }

            role.Name = name;
            role.Permissions = permissions;
        }

        await db.SaveChangesAsync(ct);
    }
}
