using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Muster.Infrastructure.Services;
using Muster.Web;

namespace Muster.Web.Components;

/// <summary>
/// Base for guild admin pages: resolves the signed-in user and guild from the route, enforces
/// admin (or officer) access, and exposes a simple state for the view. Subclasses load their data
/// in <see cref="LoadAsync"/>.
/// </summary>
public abstract class GuildAdminComponentBase : ComponentBase
{
    public enum AccessState
    {
        Loading,
        NeedLogin,
        Forbidden,
        Ready,
    }

    [Parameter] public string GuildIdRaw { get; set; } = string.Empty;

    [CascadingParameter] protected Task<AuthenticationState>? AuthState { get; set; }

    [Inject] protected GuildAuthorizationService Auth { get; set; } = default!;

    [Inject] protected AuditService Audit { get; set; } = default!;

    [Inject] protected MentionHumanizer Mentions { get; set; } = default!;

    /// <summary>Record an admin action to the audit trail (actor = the signed-in user).</summary>
    protected Task AuditAsync(string action, string? details = null)
        => Audit.RecordAsync(GuildId, UserId, action, details);

    /// <summary>Render Discord mention tokens in command output as readable names for the web.</summary>
    protected Task<string> HumanizeAsync(string? text) => Mentions.HumanizeAsync(GuildId, text);

    protected AccessState State { get; private set; } = AccessState.Loading;

    protected ulong UserId { get; private set; }

    protected ulong GuildId { get; private set; }

    protected string? Message { get; set; }

    /// <summary>When true, officer access is sufficient; otherwise admin is required.</summary>
    protected virtual bool OfficerSufficient => false;

    protected override async Task OnInitializedAsync()
    {
        if (AuthState is null)
        {
            State = AccessState.NeedLogin;
            return;
        }

        var user = (await AuthState).User;
        if (user.GetDiscordUserId() is not { } userId)
        {
            State = AccessState.NeedLogin;
            return;
        }

        UserId = userId;

        if (!ulong.TryParse(GuildIdRaw, out var guildId))
        {
            State = AccessState.Forbidden;
            return;
        }

        GuildId = guildId;

        var allowed = OfficerSufficient
            ? await Auth.IsOfficerAsync(guildId, userId)
            : await Auth.IsAdminAsync(guildId, userId);

        if (!allowed)
        {
            State = AccessState.Forbidden;
            return;
        }

        State = AccessState.Ready;
        await LoadAsync();
    }

    /// <summary>Load page data once access is confirmed. Called on init and after a successful action.</summary>
    protected virtual Task LoadAsync() => Task.CompletedTask;
}
