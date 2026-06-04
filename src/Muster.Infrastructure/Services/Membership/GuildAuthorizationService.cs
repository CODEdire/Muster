using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services.Membership;

/// <summary>
/// Resolves a member's app-level permissions from the guild's role→tier mappings
/// (<c>GuildRoleMapping</c>, one row per Discord role carrying a <see cref="GuildRoleTier"/> bitmask).
/// Admin is granted if ANY of these hold (so a misconfigured mapping can never lock everyone out):
///   1. the member is the guild owner;
///   2. the member holds a Discord role with Administrator or Manage Guild permission;
///   3. the member holds a role mapped to the <see cref="GuildRoleTier.Admin"/> tier.
/// Every other tier is a single bit on the same mapping; checks intersect the member's synced role ids with
/// the (small) per-guild map in memory.
/// </summary>
public class GuildAuthorizationService(MusterDbContext db)
{
    public async Task<bool> IsAdminAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return false;
        }

        if (userId == guild.OwnerId)
        {
            return true; // owner bypass — lockout-proof
        }

        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (member is null)
        {
            return false;
        }

        var map = await db.RoleMapAsync(guildId, ct);
        if (member.RoleIds.Any(r => map.TryGetValue(r, out var t) && t.HasFlag(GuildRoleTier.Admin)))
        {
            return true;
        }

        return await HasAdminPermissionAsync(guildId, member.RoleIds, ct);
    }

    /// <summary>Economy staff — may mint / adjust / bulk-move any currency (POINTS + COIN) and view anyone's
    /// wallet. Admin bypass.</summary>
    public async Task<bool> IsEconomyManagerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
        => await IsAdminAsync(guildId, userId, ct)
            || await HasMappedTierAsync(guildId, userId, GuildRoleTier.EconomyManager, ct);

    /// <summary>Tracking staff — open/close sessions, configure monitored channels + reward multipliers, run
    /// musters and event ops (the <c>/op</c> family), force-opt-out members. Admin bypass.</summary>
    public async Task<bool> IsTrackingManagerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
        => await IsAdminAsync(guildId, userId, ct)
            || await HasMappedTierAsync(guildId, userId, GuildRoleTier.TrackingManager, ct);

    /// <summary>May post musters from a template. Tracking managers (and admins) implicitly qualify — and they can
    /// additionally create custom musters; <see cref="GuildRoleTier.MusterCreator"/> holders are template-only.</summary>
    public async Task<bool> IsMusterCreatorAsync(ulong guildId, ulong userId, CancellationToken ct = default)
        => await IsTrackingManagerAsync(guildId, userId, ct)
            || await HasMappedTierAsync(guildId, userId, GuildRoleTier.MusterCreator, ct);

    /// <summary>Whether <paramref name="userId"/> may manage the lifecycle of a specific muster (close, edit, curate
    /// roster, remove card). A Tracking Manager (admin included) may manage <i>any</i> muster; a template-only Muster
    /// Creator may manage only the ones they <paramref name="createdBy"/> own. Session
    /// linking + coin-gate are deliberately NOT covered here — those stay Tracking-Manager-only (they affect session
    /// economics).</summary>
    public async Task<bool> CanManageMusterAsync(ulong guildId, ulong userId, ulong createdBy, CancellationToken ct = default)
    {
        if (await IsTrackingManagerAsync(guildId, userId, ct))
        {
            return true;
        }

        return createdBy == userId && await IsMusterCreatorAsync(guildId, userId, ct);
    }

    /// <summary>Read-only observer — audit log, ledger, participation. Implied by any mutating role
    /// (admin / economy manager / tracking manager / quest manager) so adding the role is purely additive.</summary>
    public async Task<bool> IsAuditorAsync(ulong guildId, ulong userId, CancellationToken ct = default)
        => await IsAdminAsync(guildId, userId, ct)
            || await IsEconomyManagerAsync(guildId, userId, ct)
            || await IsTrackingManagerAsync(guildId, userId, ct)
            || await IsQuestManagerAsync(guildId, userId, ct)
            || await HasMappedTierAsync(guildId, userId, GuildRoleTier.Auditor, ct);

    /// <summary>True if the member holds ANY staff tier (admin / economy / tracking / quest / auditor). For landing
    /// surfaces (e.g. the dashboard's Admin link) that route on to role-specific sub-areas. Equivalent to
    /// <see cref="IsAuditorAsync"/> — auditor is implied by every management role — but named for the intent.</summary>
    public Task<bool> IsAnyStaffAsync(ulong guildId, ulong userId, CancellationToken ct = default)
        => IsAuditorAsync(guildId, userId, ct);

    /// <summary>Quest managers create guild quests and approve/arbitrate player bounties. Admins are implicitly managers.</summary>
    public async Task<bool> IsQuestManagerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
        => await IsAdminAsync(guildId, userId, ct)
            || await HasMappedTierAsync(guildId, userId, GuildRoleTier.QuestManager, ct);

    /// <summary>
    /// Whether the member may earn rewards / be tracked. Participant is the <i>floor</i> of the role
    /// hierarchy — anyone with a mapped staff role (Quest Manager / Economy Manager / Tracking Manager /
    /// Auditor, plus Admin) implicitly participates, so a quest manager who can arbitrate a quest can also
    /// take one without needing a redundant Participant toggle.
    ///
    /// <para>If no role carries the Participant bit, participation is open to everyone (default); otherwise the
    /// member must hold a Participant-mapped role OR any mapped staff role.</para>
    /// </summary>
    public async Task<bool> IsParticipantAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        // Admin bypass — owners + Discord admins + mapped admins are always participants.
        if (await IsAdminAsync(guildId, userId, ct))
        {
            return true;
        }

        var map = await db.RoleMapAsync(guildId, ct);

        // Open by default: no role carries the Participant bit. @everyone (Discord stores its role id as the
        // guild id) carrying it also means everyone — synced GuildMember.RoleIds never includes @everyone since
        // Discord treats it as universal, so without this we'd fail the overlap check for every member.
        var participantRoles = map.Where(kv => kv.Value.HasFlag(GuildRoleTier.Participant)).Select(kv => kv.Key).ToHashSet();
        if (participantRoles.Count == 0 || participantRoles.Contains(guildId))
        {
            return true;
        }

        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (member is null)
        {
            return false;
        }

        // Staff implies participant (saves toggling every staff role's Participant column too); else the member
        // must hold a Participant-mapped role directly.
        const GuildRoleTier qualifying = GuildRoleTier.Participant | GuildRoleTier.QuestManager
            | GuildRoleTier.EconomyManager | GuildRoleTier.TrackingManager | GuildRoleTier.Auditor;
        return member.RoleIds.Any(r => map.TryGetValue(r, out var t) && (t & qualifying) != 0);
    }

    /// <summary>Whether the member holds any role mapped to <paramref name="tier"/> (a single bit, or a mask).
    /// Loads the small per-guild role map and intersects it with the member's synced role ids in memory.</summary>
    private async Task<bool> HasMappedTierAsync(ulong guildId, ulong userId, GuildRoleTier tier, CancellationToken ct)
    {
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (member is null || member.RoleIds.Count == 0)
        {
            return false;
        }

        var map = await db.RoleMapAsync(guildId, ct);
        return member.RoleIds.Any(r => map.TryGetValue(r, out var t) && (t & tier) != 0);
    }

    private async Task<bool> HasAdminPermissionAsync(ulong guildId, List<ulong> memberRoleIds, CancellationToken ct)
    {
        // Materialize the member's role permissions, then test bits client-side (SQL Server can't do
        // bitwise AND on the decimal-mapped ulong column).
        var roles = await db.RolePermissionsAsync(guildId, memberRoleIds, ct);
        return roles.Any(p => (p & DiscordPermissions.AdminBypassMask) != 0);
    }
}
