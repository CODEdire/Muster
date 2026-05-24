using Microsoft.EntityFrameworkCore;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

public record MemberOption(ulong UserId, string DisplayName);

public record PendingApproval(Guid MissionId, string QuestName, ulong UserId, string DisplayName);

public record RoleOption(ulong RoleId, string Name);

public record RoleMappingView(
    IReadOnlyList<RoleOption> AllRoles,
    IReadOnlyList<ulong> AdminRoleIds,
    IReadOnlyList<ulong> OfficerRoleIds,
    IReadOnlyList<ulong> ParticipantRoleIds,
    IReadOnlyList<ulong> QuestManagerRoleIds);

/// <summary>Read models backing the web admin consoles (member pickers, approval queue, role mapping).</summary>
public class WebAdminService(MusterDbContext db)
{
    public async Task<IReadOnlyList<MemberOption>> GetMembersAsync(ulong guildId, CancellationToken ct = default)
    {
        var members = await db.GuildMembers.Where(m => m.GuildId == guildId).ToListAsync(ct);
        var names = await ResolveNamesAsync(members.Select(m => m.UserId), ct);

        return members
            .Select(m => new MemberOption(m.UserId, m.Nickname ?? names.GetValueOrDefault(m.UserId, m.UserId.ToString())))
            .OrderBy(o => o.DisplayName)
            .ToList();
    }

    public async Task<IReadOnlyList<PendingApproval>> GetPendingApprovalsAsync(ulong guildId, CancellationToken ct = default)
    {
        var pending = await (
            from p in db.MissionParticipants
            join m in db.Missions on p.MissionId equals m.Id
            where m.GuildId == guildId && m.Type == MissionType.Quest && p.Status == MissionParticipantStatus.Submitted
            select new { p.MissionId, QuestName = m.Name, p.UserId }).ToListAsync(ct);

        var names = await ResolveNamesAsync(pending.Select(p => p.UserId), ct);

        return pending
            .Select(p => new PendingApproval(p.MissionId, p.QuestName, p.UserId, names.GetValueOrDefault(p.UserId, p.UserId.ToString())))
            .ToList();
    }

    public async Task<RoleMappingView> GetRoleMappingAsync(ulong guildId, CancellationToken ct = default)
    {
        var roles = await db.GuildRoles
            .Where(r => r.GuildId == guildId)
            .OrderBy(r => r.Name)
            .Select(r => new RoleOption(r.RoleId, r.Name))
            .ToListAsync(ct);

        var guild = await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct);
        return new RoleMappingView(
            roles,
            guild?.Settings.AdminRoleIds ?? [],
            guild?.Settings.OfficerRoleIds ?? [],
            guild?.Settings.ParticipantRoleIds ?? [],
            guild?.Settings.QuestManagerRoleIds ?? []);
    }

    private async Task<Dictionary<ulong, string>> ResolveNamesAsync(IEnumerable<ulong> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Users
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.GlobalName ?? u.Username, ct);
    }
}
