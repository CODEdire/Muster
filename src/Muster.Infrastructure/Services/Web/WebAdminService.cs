using Muster.Domain.Entities;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Web;

public record MemberOption(ulong UserId, string DisplayName);

public record RoleOption(ulong RoleId, string Name);

public record RoleMappingView(
    IReadOnlyList<RoleOption> AllRoles,
    IReadOnlyList<ulong> AdminRoleIds,
    IReadOnlyList<ulong> ParticipantRoleIds,
    IReadOnlyList<ulong> QuestManagerRoleIds,
    IReadOnlyList<ulong> EconomyManagerRoleIds,
    IReadOnlyList<ulong> TrackingManagerRoleIds,
    IReadOnlyList<ulong> MusterCreatorRoleIds,
    IReadOnlyList<ulong> AuditorRoleIds);

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

    /// <summary>Members eligible to be a muster participant — those holding a configured participant role (or every
    /// member when participation is open, i.e. no participant roles set). For the muster roster's add-member picker.</summary>
    public async Task<IReadOnlyList<MemberOption>> GetParticipantMembersAsync(ulong guildId, CancellationToken ct = default)
    {
        var all = await GetMembersAsync(guildId, ct);

        var map = await db.RoleMapAsync(guildId, ct);
        var roleIds = map.Where(kv => kv.Value.HasFlag(GuildRoleTier.Participant)).Select(kv => kv.Key).ToHashSet();
        if (roleIds.Count == 0)
        {
            return all; // open participation
        }

        var members = await db.ListMembersAsync(guildId, ct);
        var eligible = members
            .Where(m => m.RoleIds.Any(r => roleIds.Contains(r)))
            .Select(m => m.UserId)
            .ToHashSet();

        return all.Where(o => eligible.Contains(o.UserId)).ToList();
    }

    /// <summary>Current ledger retention setting for the guild (0 = inherit platform default).</summary>
    public async Task<int> GetLedgerRetentionDaysAsync(ulong guildId, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        return guild?.Settings.LedgerRetentionDays ?? 0;
    }

    /// <summary>Full guild settings snapshot for the config pages. Returns a new default when the guild isn't
    /// provisioned yet so the form can still bind.</summary>
    public async Task<GuildSettings> GetSettingsAsync(ulong guildId, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        return guild?.Settings ?? new GuildSettings();
    }

    /// <summary>All tracked voice/text channels on the guild, in their stored order.</summary>
    public Task<List<GuildChannel>> GetTrackedChannelsAsync(ulong guildId, CancellationToken ct = default)
        => db.ListTrackedChannelsAsync(guildId, ct);

    /// <summary>True when the guild has at least one live voice channel set to a non-Off tracked mode.</summary>
    public Task<bool> HasActiveBackgroundTrackingAsync(ulong guildId, CancellationToken ct = default)
        => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            db.GuildChannels.Where(c => c.GuildId == guildId
                && c.Kind == GuildChannelKind.Voice
                && c.Mode != TrackedChannelMode.Off
                && c.DeletedAt == null),
            ct);

    /// <summary>Synced channels of one kind (e.g. voice for session pickers, text for quest channels).</summary>
    public Task<List<GuildChannel>> GetChannelsByKindAsync(ulong guildId, GuildChannelKind kind, CancellationToken ct = default)
        => db.ListChannelsByKindAsync(guildId, kind, ct);

    /// <summary>Simple (RoleId, Name) list for role pickers — no settings context.</summary>
    public async Task<IReadOnlyList<RoleOption>> GetRolesAsync(ulong guildId, CancellationToken ct = default)
    {
        var roles = await db.ListRolesAsync(guildId, ct);
        return roles.OrderBy(r => r.Name).Select(r => new RoleOption(r.RoleId, r.Name)).ToList();
    }

    public async Task<RoleMappingView> GetRoleMappingAsync(ulong guildId, CancellationToken ct = default)
    {
        var roles = (await db.ListRolesAsync(guildId, ct))
            .OrderBy(r => r.Name)
            .Select(r => new RoleOption(r.RoleId, r.Name))
            .ToList();

        var map = await db.RoleMapAsync(guildId, ct);
        List<ulong> ids(GuildRoleTier tier) => map.Where(kv => kv.Value.HasFlag(tier)).Select(kv => kv.Key).ToList();

        return new RoleMappingView(
            roles,
            ids(GuildRoleTier.Admin),
            ids(GuildRoleTier.Participant),
            ids(GuildRoleTier.QuestManager),
            ids(GuildRoleTier.EconomyManager),
            ids(GuildRoleTier.TrackingManager),
            ids(GuildRoleTier.MusterCreator),
            ids(GuildRoleTier.Auditor));
    }
}
