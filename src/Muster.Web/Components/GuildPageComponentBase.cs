using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;
using Muster.Web;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Web.Components;

/// <summary>
/// Shared base for guild-scoped pages. Resolves the signed-in user and guild from the route and
/// gates access via <see cref="IsAuthorizedAsync"/>. Handles two cross-cutting cases:
///   - expired/absent session → redirect to login with a return URL (seamless re-auth);
///   - authenticated but not allowed → friendly Forbidden state + HTTP 403 status.
/// </summary>
public abstract class GuildPageComponentBase : ComponentBase
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

    // Cascaded by the framework for statically-rendered components.
    [CascadingParameter] public HttpContext? Http { get; set; }

    [Inject] protected GuildAuthorizationService Auth { get; set; } = default!;

    [Inject] protected MemberSyncService MemberSync { get; set; } = default!;

    [Inject] protected Muster.Infrastructure.Services.Platform.TimeZoneService TimeZones { get; set; } = default!;

    [Inject] protected NavigationManager Nav { get; set; } = default!;

    [Inject] protected IJSRuntime JS { get; set; } = default!;

    [Inject] protected BrowserTimeZone Browser { get; set; } = default!;

    /// <summary>The viewer's browser IANA zone (shared cache; detected after the circuit connects on interactive
    /// pages). Null until detected/unavailable. <see cref="LocalTime"/> uses this itself — pages only need it for
    /// non-component formatting (e.g. a zone label or a string built in code).</summary>
    protected string? BrowserZoneId => Browser.Zone;

    protected AccessState State { get; private set; } = AccessState.Loading;

    protected ulong UserId { get; private set; }

    protected ulong GuildId { get; private set; }

    protected string? Message { get; set; }

    protected abstract Task<bool> IsAuthorizedAsync(ulong guildId, ulong userId);

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected override async Task OnInitializedAsync()
    {
        var principal = AuthState is null ? null : (await AuthState).User;
        var userId = principal?.GetDiscordUserId();
        if (userId is null)
        {
            // Session missing or expired — bounce through login and come back here.
            State = AccessState.NeedLogin;
            var returnUrl = "/" + Nav.ToBaseRelativePath(Nav.Uri);
            Nav.NavigateTo($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        UserId = userId.Value;

        // Keep the signed-in user's profile from showing as a raw id even before a gateway/login sync —
        // fills name from claims, preserving any avatar/global name already synced. Idempotent + cheap.
        if (principal!.Identity?.Name is { Length: > 0 } username)
        {
            await MemberSync.EnsureUserAsync(UserId, username);
        }

        if (!ulong.TryParse(GuildIdRaw, out var guildId))
        {
            Forbid();
            return;
        }

        GuildId = guildId;

        if (!await IsAuthorizedAsync(guildId, UserId))
        {
            Forbid();
            return;
        }

        // New-user onboarding: a member with no time zone set is bounced to /onboarding (and back here after),
        // so dates they enter are never silently misread.
        if (!await TimeZones.HasUserZoneAsync(UserId))
        {
            var returnUrl = "/" + Nav.ToBaseRelativePath(Nav.Uri);
            Nav.NavigateTo($"/onboarding?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        State = AccessState.Ready;
        await LoadAsync();
    }

    // After the circuit connects (interactive pages), make sure the shared browser-zone cache is populated, then
    // re-render so page-side zone labels/strings pick it up. No-op on static SSR (this never runs).
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && State == AccessState.Ready)
        {
            await Browser.EnsureAsync(JS);
            StateHasChanged();
        }
    }

    private void Forbid()
    {
        State = AccessState.Forbidden;
        if (Http is { Response.HasStarted: false } http)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }
}
