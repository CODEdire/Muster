using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Web;

public record MemberOption(ulong UserId, string DisplayName);

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
        var members = await db.ListMembersAsync(guildId, ct);

        // Bots are synced (so they can be API service actors) but shouldn't appear in human award/role pickers.
        var botIds = await db.BotUserIdsAsync(members.Select(m => m.UserId).ToList(), ct);
        members = members.Where(m => !botIds.Contains(m.UserId)).ToList();

        var ids = members.Select(m => m.UserId).ToList();

        // The owner is an admin/award recipient even without a synced GuildMember row, so always include them.
        var ownerId = await db.GuildOwnerIdAsync(guildId, ct);
        var includeOwner = ownerId != 0 && !ids.Contains(ownerId);
        if (includeOwner)
        {
            ids.Add(ownerId);
        }

        var names = await db.UserDisplayNameMapAsync(ids.Distinct().ToList(), ct);

        var options = members
            .Select(m => new MemberOption(m.UserId, m.Nickname ?? names.GetValueOrDefault(m.UserId, m.UserId.ToString())))
            .ToList();

        if (includeOwner)
        {
            options.Add(new MemberOption(ownerId, names.GetValueOrDefault(ownerId, ownerId.ToString())));
        }

        return options.OrderBy(o => o.DisplayName).ToList();
    }

    public async Task<RoleMappingView> GetRoleMappingAsync(ulong guildId, CancellationToken ct = default)
    {
        var roles = (await db.ListRolesAsync(guildId, ct))
            .OrderBy(r => r.Name)
            .Select(r => new RoleOption(r.RoleId, r.Name))
            .ToList();

        var guild = await db.FindGuildAsync(guildId, ct);
        return new RoleMappingView(
            roles,
            guild?.Settings.AdminRoleIds ?? [],
            guild?.Settings.OfficerRoleIds ?? [],
            guild?.Settings.ParticipantRoleIds ?? [],
            guild?.Settings.QuestManagerRoleIds ?? []);
    }
}
