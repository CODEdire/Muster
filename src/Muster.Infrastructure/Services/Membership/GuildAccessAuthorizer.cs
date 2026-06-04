namespace Muster.Infrastructure.Services.Membership;

/// <summary>
/// Which guild roles are sufficient to view a staff page. Combine with <c>|</c> when a page is open to several
/// management tiers (e.g. <c>EconomyManager | Auditor</c> for a read-mostly ledger).
/// </summary>
/// <remarks>
/// <para>Admin always passes implicitly — the lockout-proof owner / Discord-admin-perm / mapped admin role works
/// for every page. The flags here describe which <b>additional</b> tiers also pass.</para>
/// <para>Each flag maps to one dedicated role list (Economy / Tracking / Quest / Auditor). Pages whose action mix
/// spans tiers (e.g. an admin landing) can use <see cref="AnyStaff"/>.</para>
/// </remarks>
[Flags]
public enum GuildAccessTier
{
    /// <summary>Admin-only (default). Listed for explicitness; admin always passes anyway.</summary>
    Admin            = 0,

    /// <summary>Mint / adjust / view-anyone wallets. Covers POINTS + COIN equally.</summary>
    EconomyManager   = 1 << 1,

    /// <summary>Tracking config — sessions, monitored channels, multipliers, event ops (the <c>/op</c> family).</summary>
    TrackingManager  = 1 << 3,

    /// <summary>Quest workflow management (intake, arbitration, finalize).</summary>
    QuestManager     = 1 << 4,

    /// <summary>Read-only observer (audit log, ledger, participation). Implied by any management role server-side,
    /// but pages must still list it so an explicit-auditor-only viewer can reach the page.</summary>
    Auditor          = 1 << 5,

    /// <summary>May post musters from a template (template-only self-service). Tracking managers imply it.</summary>
    MusterCreator    = 1 << 6,

    /// <summary>Any staff tier — use on landing pages that link out to role-specific sub-areas (admin nav).</summary>
    AnyStaff         = EconomyManager | TrackingManager | QuestManager | Auditor,
}

/// <summary>
/// Dispatch helper for <see cref="GuildAccessTier"/>. Pure-function check that the Blazor base class
/// (<c>GuildAdminComponentBase</c>) delegates to, kept here so the OR-over-role-checks logic is testable without
/// spinning up a render context. Admin always passes; each set flag triggers the matching
/// <see cref="GuildAuthorizationService"/> check; the first match wins.
/// </summary>
public static class GuildAccessAuthorizer
{
    public static async Task<bool> IsAuthorizedAsync(
        GuildAuthorizationService roles, ulong guildId, ulong userId, GuildAccessTier required, CancellationToken ct = default)
    {
        // Admin always passes — owner bypass + Discord Administrator / Manage-Guild + mapped AdminRoleIds.
        if (await roles.IsAdminAsync(guildId, userId, ct))
        {
            return true;
        }

        if (required == GuildAccessTier.Admin)
        {
            return false; // admin-only and they're not admin
        }

        // Hottest-first ordering: Economy + Tracking are the most-used tiers; cold tiers last.
        if (required.HasFlag(GuildAccessTier.EconomyManager)  && await roles.IsEconomyManagerAsync(guildId, userId, ct)) return true;
        if (required.HasFlag(GuildAccessTier.TrackingManager) && await roles.IsTrackingManagerAsync(guildId, userId, ct)) return true;
        if (required.HasFlag(GuildAccessTier.MusterCreator)    && await roles.IsMusterCreatorAsync(guildId, userId, ct)) return true;
        if (required.HasFlag(GuildAccessTier.QuestManager)    && await roles.IsQuestManagerAsync(guildId, userId, ct)) return true;
        if (required.HasFlag(GuildAccessTier.Auditor)         && await roles.IsAuditorAsync(guildId, userId, ct)) return true;
        return false;
    }
}
