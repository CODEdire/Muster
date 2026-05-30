using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Components;

/// <summary>
/// Base for guild admin pages: gates access via <see cref="RequiredAccess"/>, adds audit recording + mention
/// humanizing helpers. Session / 403 handling comes from the base. Admin always passes via the lockout-proof
/// bypass in <see cref="GuildAuthorizationService"/>. The <see cref="GuildAccessTier"/> flags + dispatch live in
/// <see cref="GuildAccessAuthorizer"/> (Muster.Infrastructure) so the OR-over-roles logic is testable without a
/// Blazor render context.
/// </summary>
public abstract class GuildAdminComponentBase : GuildPageComponentBase
{
    [Inject] protected AuditService Audit { get; set; } = default!;

    [Inject] protected MentionHumanizer Mentions { get; set; } = default!;

    /// <summary>Which non-admin tiers may also access this page. Default = <see cref="GuildAccessTier.Admin"/>
    /// (admin only). Override on pages that should accept a Phase 4 split role.</summary>
    protected virtual GuildAccessTier RequiredAccess => GuildAccessTier.Admin;

    protected override Task<bool> IsAuthorizedAsync(ulong guildId, ulong userId)
        => GuildAccessAuthorizer.IsAuthorizedAsync(Auth, guildId, userId, RequiredAccess);

    /// <summary>Record an admin action to the audit trail (actor = the signed-in user). Prefer the
    /// <see cref="AuditAction"/> overload — passing a string here goes through unrecognized to the catch-all
    /// category in the audit page.</summary>
    protected Task AuditAsync(string action, string? details = null)
        => Audit.RecordAsync(GuildId, UserId, action, details);

    /// <summary>Record an admin action with a registered <see cref="AuditAction"/> (category + source baked in).</summary>
    protected Task AuditAsync(AuditAction action, string? details = null)
        => Audit.RecordAsync(GuildId, UserId, action, details);

    /// <summary>Record an admin action with a typed payload — serialized to JSON and stored in <c>Details</c>.</summary>
    protected Task AuditAsync<T>(AuditAction action, T payload)
        => Audit.RecordAsync(GuildId, UserId, action, payload);

    /// <summary>Render Discord mention tokens in command output as readable names for the web.</summary>
    protected Task<string> HumanizeAsync(string? text) => Mentions.HumanizeAsync(GuildId, text);
}
