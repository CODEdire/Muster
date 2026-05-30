using Muster.Domain;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Tracking;

/// <summary>An action a <see cref="GuildActor"/> may attempt against the tracking surface — the authorization unit.</summary>
public enum TrackingPermission
{
    /// <summary>Open/close a tracking session, force-opt-out another member from one.</summary>
    ManageSessions,

    /// <summary>Configure or remove monitored voice/text channels.</summary>
    ManageChannels,

    /// <summary>Create / enable / disable / remove reward multipliers.</summary>
    ManageMultipliers,

    /// <summary>Set a member's per-guild tracking/privacy preference. Self always; others = staff only.</summary>
    SetPrivacy,

    /// <summary>Read a member's voice participation stats. Self always; others = staff only (TrackingManager or auditor tier).</summary>
    ViewMemberStats,
}

/// <summary>
/// Resource-based authorization for tracking, mirroring <see cref="ICurrencyAuthorizer"/> and <c>IQuestAuthorizer</c>:
/// the single place the staff-vs-self rules live so command handlers (before mutating) and any future UI gating can't
/// drift. Staff = admin OR officer (the "tracking-management tier"). The subject only matters for
/// <see cref="TrackingPermission.SetPrivacy"/>; other actions ignore it.
/// </summary>
public interface ITrackingAuthorizer
{
    Task<bool> AuthorizeAsync(GuildActor actor, ulong subjectUserId, TrackingPermission action, CancellationToken ct = default);

    /// <summary>The pure rule, with the staff flag already resolved.</summary>
    bool Allows(GuildActor actor, bool isStaff, ulong subjectUserId, TrackingPermission action);
}

public sealed class TrackingAuthorizer(GuildAuthorizationService roles) : ITrackingAuthorizer
{
    public async Task<bool> AuthorizeAsync(GuildActor actor, ulong subjectUserId, TrackingPermission action, CancellationToken ct = default)
    {
        // TrackingManager = admin-inclusive + legacy OfficerRoleIds alias (back-compat). Replaces the prior
        // IsOfficerAsync gate so a guild can grant tracking power without granting economy-staff power.
        var isStaff = await roles.IsTrackingManagerAsync(actor.GuildId, actor.UserId, ct);
        return Allows(actor, isStaff, subjectUserId, action);
    }

    public bool Allows(GuildActor actor, bool isStaff, ulong subjectUserId, TrackingPermission action) => action switch
    {
        TrackingPermission.ManageSessions
            or TrackingPermission.ManageChannels
            or TrackingPermission.ManageMultipliers => isStaff,

        TrackingPermission.SetPrivacy or TrackingPermission.ViewMemberStats
            => subjectUserId == actor.UserId || isStaff,

        _ => false,
    };
}
