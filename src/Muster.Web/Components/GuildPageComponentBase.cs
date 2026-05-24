using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Muster.Infrastructure.Services;
using Muster.Web;

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

    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected AccessState State { get; private set; } = AccessState.Loading;

    protected ulong UserId { get; private set; }

    protected ulong GuildId { get; private set; }

    protected string? Message { get; set; }

    protected abstract Task<bool> IsAuthorizedAsync(ulong guildId, ulong userId);

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected override async Task OnInitializedAsync()
    {
        var userId = AuthState is null ? null : (await AuthState).User.GetDiscordUserId();
        if (userId is null)
        {
            // Session missing or expired — bounce through login and come back here.
            State = AccessState.NeedLogin;
            var returnUrl = "/" + Nav.ToBaseRelativePath(Nav.Uri);
            Nav.NavigateTo($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        UserId = userId.Value;

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

        State = AccessState.Ready;
        await LoadAsync();
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
