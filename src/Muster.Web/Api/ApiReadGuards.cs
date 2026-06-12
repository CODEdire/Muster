using Muster.Domain;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Web.Api;

/// <summary>
/// Inline authorization guards for API <b>read</b> endpoints. Writes already funnel through their CQRS authorizers
/// (CurrencyAuthorizer / QuestAuthorizer / TrackingAuthorizer); reads historically only gated on scope, so a
/// scope-only key could read personal data (any member's wallet/ledger/tracking) or staff-tier data (full guild
/// ledger, audit log, presence event stream). These helpers close that gap by adding a resource-auth or role check
/// per request. Endpoint is responsible for declaring <c>requireActor: true</c> on the scope attribute so the actor
/// is always present here.
/// </summary>
public static class ApiReadGuards
{
    /// <summary>403 forbidden helper.</summary>
    public static IResult Forbid(string detail = "forbidden") =>
        Results.Json(new { error = detail }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Wallet/ledger read gate: own always; anyone's for economy staff or read-only auditors. Delegates to
    /// <see cref="CurrencyPermission.View"/> so the self / manager / auditor rule stays defined in one place.</summary>
    public static async Task<IResult?> RequireSelfOrEconomyStaffAsync(
        HttpContext http, ulong guildId, ulong subjectUserId, ICurrencyAuthorizer currency, CancellationToken ct = default)
    {
        var actor = http.ApiActor();
        return await currency.AuthorizeAsync(new GuildActor(guildId, actor), subjectUserId, CurrencyPermission.View, ct)
            ? null
            : Forbid();
    }

    /// <summary>Self-or-tracking-staff: actor may read own stats, or anyone's if tracking manager or auditor tier.</summary>
    public static async Task<IResult?> RequireSelfOrTrackingStaffAsync(
        HttpContext http, ulong guildId, ulong subjectUserId, ITrackingAuthorizer tracking, CancellationToken ct = default)
    {
        var actor = http.ApiActor();
        return await tracking.AuthorizeAsync(new GuildActor(guildId, actor), subjectUserId, TrackingPermission.ViewMemberStats, ct)
            ? null
            : Forbid();
    }

    /// <summary>Auditor tier — any management role (admin / officer / economy / event / tracking / quest) or
    /// explicit auditor-role member. Use for staff-only reads that have no per-subject dimension (full guild ledger,
    /// audit log, presence event stream, supply analytics).</summary>
    public static async Task<IResult?> RequireAuditorAsync(
        HttpContext http, ulong guildId, GuildAuthorizationService roles, CancellationToken ct = default)
    {
        var actor = http.ApiActor();
        return await roles.IsAuditorAsync(guildId, actor, ct) ? null : Forbid();
    }

    /// <summary>Admin only — bot operators / guild owner / mapped admin role. Use for the highest-impact endpoints
    /// (machine-inbound mint/spend), which an arbitrary scoped key must NOT reach. Connector bots that need mint/spend
    /// must be bound to an actor that holds an admin role on the guild (or to the guild owner).</summary>
    public static async Task<IResult?> RequireAdminAsync(
        HttpContext http, ulong guildId, GuildAuthorizationService roles, CancellationToken ct = default)
    {
        var actor = http.ApiActor();
        return await roles.IsAdminAsync(guildId, actor, ct) ? null : Forbid();
    }
}
