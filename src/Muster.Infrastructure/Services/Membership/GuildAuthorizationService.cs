using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain;

namespace Muster.Infrastructure.Services.Membership;

/// <summary>
/// Resolves a member's app-level permissions. Admin is granted if ANY of these hold (so a misconfigured
/// role mapping can never lock everyone out):
///   1. the member is the guild owner;
///   2. the member holds a Discord role with Administrator or Manage Guild permission;
///   3. the member holds a role configured in <c>GuildSettings.AdminRoleIds</c>.
/// Officer additionally includes <c>OfficerRoleIds</c>. Both rely on the synced role/member snapshots.
/// </summary>
public class GuildAuthorizationService(MusterDbContext db)
{
    public async Task<bool> IsAdminAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        if (guild is null)
        {
            return false;
        }

        if (userId == guild.OwnerId)
        {
            return true; // owner bypass — lockout-proof
        }

        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        if (member is null)
        {
            return false;
        }

        if (member.RoleIds.Any(r => guild.Settings.AdminRoleIds.Contains(r)))
        {
            return true;
        }

        return await HasAdminPermissionAsync(guildId, member.RoleIds, ct);
    }

    public async Task<bool> IsOfficerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        if (await IsAdminAsync(guildId, userId, ct))
        {
            return true;
        }

        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r => guild.Settings.OfficerRoleIds.Contains(r));
    }

    /// <summary>Quest managers create guild quests and approve/arbitrate player bounties. Admins are implicitly managers.</summary>
    public async Task<bool> IsQuestManagerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        if (await IsAdminAsync(guildId, userId, ct))
        {
            return true;
        }

        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r =>
            guild.Settings.QuestManagerRoleIds.Contains(r) || guild.Settings.OfficerRoleIds.Contains(r));
    }

    /// <summary>
    /// Whether the member may earn rewards / be tracked. If no participant roles are configured,
    /// participation is open to everyone (default); otherwise the member must hold a configured role.
    /// </summary>
    public async Task<bool> IsParticipantAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        if (guild is null)
        {
            return false;
        }

        var allowed = guild.Settings.ParticipantRoleIds;
        if (allowed.Count == 0)
        {
            return true; // open by default
        }

        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        return member is not null && member.RoleIds.Any(r => allowed.Contains(r));
    }

    private async Task<bool> HasAdminPermissionAsync(ulong guildId, List<ulong> memberRoleIds, CancellationToken ct)
    {
        // Materialize the member's roles, then test permission bits client-side (SQL Server can't do
        // bitwise AND on the decimal-mapped ulong column).
        var roles = await db.GuildRoles
            .Where(r => r.GuildId == guildId && memberRoleIds.Contains(r.RoleId))
            .Select(r => r.Permissions)
            .ToListAsync(ct);

        return roles.Any(p => (p & DiscordPermissions.AdminBypassMask) != 0);
    }
}
