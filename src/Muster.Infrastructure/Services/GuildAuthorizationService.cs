using Microsoft.EntityFrameworkCore;

namespace Muster.Infrastructure.Services;

/// <summary>
/// Resolves a member's app-level permissions from the guild's role mapping. Permissions map to
/// **Discord role ids** configured in <c>GuildSettings</c> (AdminRoleIds / OfficerRoleIds) intersected
/// with the member's synced role snapshot — never to individual users. Keeping
/// <c>GuildMember.RoleIds</c> in sync (via the member-update handler) keeps these answers correct.
/// </summary>
public class GuildAuthorizationService(MusterDbContext db)
{
    /// <summary>True if the member holds any configured admin role.</summary>
    public async Task<bool> IsAdminAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r => guild.Settings.AdminRoleIds.Contains(r));
    }

    /// <summary>True if the member holds any configured officer role (admins are implicitly officers).</summary>
    public async Task<bool> IsOfficerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r =>
            guild.Settings.OfficerRoleIds.Contains(r) || guild.Settings.AdminRoleIds.Contains(r));
    }
}
