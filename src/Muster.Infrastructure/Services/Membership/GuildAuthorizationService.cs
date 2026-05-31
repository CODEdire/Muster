using Muster.Persistence;
using Muster.Persistence.Queries;
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

        var guild = await db.FindGuildAsync(guildId, ct);
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r => guild.Settings.OfficerRoleIds.Contains(r));
    }

    /// <summary>Economy staff — may mint / adjust / bulk-move any currency (POINTS + COIN) and view anyone's
    /// wallet. Admin bypass; legacy <see cref="GuildSettings.OfficerRoleIds"/> also grants this (back-compat).</summary>
    public async Task<bool> IsEconomyManagerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        if (await IsAdminAsync(guildId, userId, ct))
        {
            return true;
        }

        var guild = await db.FindGuildAsync(guildId, ct);
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r =>
            guild.Settings.EconomyManagerRoleIds.Contains(r) ||
            guild.Settings.OfficerRoleIds.Contains(r));
    }

    /// <summary>Tracking staff — open/close sessions + ops, configure monitored channels + reward multipliers,
    /// run musters, force-opt-out members. Admin bypass; the legacy <see cref="GuildSettings.OfficerRoleIds"/> and
    /// <see cref="GuildSettings.EventOfficerRoleIds"/> umbrellas also grant it. (Event Officer was merged into
    /// Tracking Manager — its role list is kept as a back-compat alias so existing mappings keep working.)</summary>
    public async Task<bool> IsTrackingManagerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        if (await IsAdminAsync(guildId, userId, ct))
        {
            return true;
        }

        var guild = await db.FindGuildAsync(guildId, ct);
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r =>
            guild.Settings.TrackingManagerRoleIds.Contains(r) ||
            guild.Settings.EventOfficerRoleIds.Contains(r) ||
            guild.Settings.OfficerRoleIds.Contains(r));
    }

    /// <summary>Event-ops staff. <b>Merged into Tracking Manager</b> — this is now an alias so the <c>/op</c> family
    /// and any EventOfficer-gated surface accept the same holders as tracking. Kept as a method for call-site
    /// stability.</summary>
    public Task<bool> IsEventOfficerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
        => IsTrackingManagerAsync(guildId, userId, ct);

    /// <summary>May post musters from a template. Tracking managers (and admins) implicitly qualify — and they can
    /// additionally create custom musters; <see cref="GuildSettings.MusterCreatorRoleIds"/> holders are template-only.</summary>
    public async Task<bool> IsMusterCreatorAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        if (await IsTrackingManagerAsync(guildId, userId, ct))
        {
            return true;
        }

        var guild = await db.FindGuildAsync(guildId, ct);
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r => guild.Settings.MusterCreatorRoleIds.Contains(r));
    }

    /// <summary>Read-only observer — audit log, ledger, participation. Implied by any mutating role
    /// (admin / officer / economy manager / event officer / tracking manager / quest manager) so adding the role
    /// is purely additive.</summary>
    public async Task<bool> IsAuditorAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        if (await IsAdminAsync(guildId, userId, ct) ||
            await IsEconomyManagerAsync(guildId, userId, ct) ||
            await IsEventOfficerAsync(guildId, userId, ct) ||
            await IsTrackingManagerAsync(guildId, userId, ct) ||
            await IsQuestManagerAsync(guildId, userId, ct))
        {
            return true;
        }

        var guild = await db.FindGuildAsync(guildId, ct);
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r => guild.Settings.AuditorRoleIds.Contains(r));
    }

    /// <summary>Quest managers create guild quests and approve/arbitrate player bounties. Admins are implicitly managers.</summary>
    public async Task<bool> IsQuestManagerAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        if (await IsAdminAsync(guildId, userId, ct))
        {
            return true;
        }

        var guild = await db.FindGuildAsync(guildId, ct);
        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (guild is null || member is null)
        {
            return false;
        }

        return member.RoleIds.Any(r =>
            guild.Settings.QuestManagerRoleIds.Contains(r) || guild.Settings.OfficerRoleIds.Contains(r));
    }

    /// <summary>
    /// Whether the member may earn rewards / be tracked. Participant is the <i>floor</i> of the role
    /// hierarchy — anyone with a mapped staff role (Admin / Officer / Quest Manager / Economy Manager /
    /// Tracking Manager / Event Officer / Auditor) implicitly participates, so a quest manager who can
    /// arbitrate a quest can also take one without needing a redundant Participant toggle.
    ///
    /// <para>If no explicit participant roles are configured, participation is open to everyone (default);
    /// otherwise the member must hold one of the configured participant roles OR any mapped staff role.</para>
    /// </summary>
    public async Task<bool> IsParticipantAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        // Admin bypass — owners + Discord admins + mapped admins are always participants.
        if (await IsAdminAsync(guildId, userId, ct))
        {
            return true;
        }

        var guild = await db.FindGuildAsync(guildId, ct);
        if (guild is null)
        {
            return false;
        }

        var settings = guild.Settings;
        var allowed = settings.ParticipantRoleIds;
        if (allowed.Count == 0 || allowed.Contains(guildId))
        {
            // Empty list = open by default. @everyone (Discord stores its role id as the guild id) being
            // explicitly toggled also means everyone — synced GuildMember.RoleIds never includes @everyone
            // since Discord treats it as universal, so without this we'd fail the role-overlap check for
            // every member even though the admin clearly opted in via the role-mapping matrix.
            return true;
        }

        var member = await db.FindMemberAsync(guildId, userId, ct);
        if (member is null)
        {
            return false;
        }

        // Staff implies participant — any mapped role on Officer/Quest/Economy/Tracking/Event/Auditor
        // counts. Saves the admin from having to toggle every staff role's Participant column too.
        if (member.RoleIds.Any(r =>
            settings.OfficerRoleIds.Contains(r) ||
            settings.QuestManagerRoleIds.Contains(r) ||
            settings.EconomyManagerRoleIds.Contains(r) ||
            settings.TrackingManagerRoleIds.Contains(r) ||
            settings.EventOfficerRoleIds.Contains(r) ||
            settings.AuditorRoleIds.Contains(r)))
        {
            return true;
        }

        return member.RoleIds.Any(r => allowed.Contains(r));
    }

    private async Task<bool> HasAdminPermissionAsync(ulong guildId, List<ulong> memberRoleIds, CancellationToken ct)
    {
        // Materialize the member's role permissions, then test bits client-side (SQL Server can't do
        // bitwise AND on the decimal-mapped ulong column).
        var roles = await db.RolePermissionsAsync(guildId, memberRoleIds, ct);
        return roles.Any(p => (p & DiscordPermissions.AdminBypassMask) != 0);
    }
}
