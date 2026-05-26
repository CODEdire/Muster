using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Components;

/// <summary>
/// Base for guild admin pages: requires admin (or officer when <see cref="OfficerSufficient"/> is true),
/// and adds audit recording + mention humanizing helpers. Session/403 handling comes from the base.
/// </summary>
public abstract class GuildAdminComponentBase : GuildPageComponentBase
{
    [Inject] protected AuditService Audit { get; set; } = default!;

    [Inject] protected MentionHumanizer Mentions { get; set; } = default!;

    /// <summary>When true, officer access is sufficient; otherwise admin is required.</summary>
    protected virtual bool OfficerSufficient => false;

    protected override async Task<bool> IsAuthorizedAsync(ulong guildId, ulong userId)
        => OfficerSufficient
            ? await Auth.IsOfficerAsync(guildId, userId)
            : await Auth.IsAdminAsync(guildId, userId);

    /// <summary>Record an admin action to the audit trail (actor = the signed-in user).</summary>
    protected Task AuditAsync(string action, string? details = null)
        => Audit.RecordAsync(GuildId, UserId, action, details);

    /// <summary>Render Discord mention tokens in command output as readable names for the web.</summary>
    protected Task<string> HumanizeAsync(string? text) => Mentions.HumanizeAsync(GuildId, text);
}
