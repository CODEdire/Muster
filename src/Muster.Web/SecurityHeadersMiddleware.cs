namespace Muster.Web;

/// <summary>
/// Sets response security headers (CSP, X-Content-Type-Options, Referrer-Policy, Permissions-Policy) on every
/// outgoing response. Razor's HTML encoding already prevents stored/reflected XSS; CSP is defence-in-depth that
/// also blunts clickjacking, mixed content, and lookalike-content via dangling external scripts.
/// </summary>
/// <remarks>
/// <para>Why a middleware instead of <c>UseStaticFiles + .csp config</c>: the API + Razor pages share one
/// pipeline and we want headers on every response (404s, error pages, API JSON, static assets) — the simplest
/// way to guarantee that is <c>OnStarting</c>, which fires once per response right before the headers go out.</para>
/// <para>CSP carries <c>unsafe-inline</c> for both scripts and styles because <see cref="Components.App"/> ships
/// inline scripts (theme detect, browser-zone fn, onclick handlers on the reconnect modal) and Blazor SSR
/// streams inline style blocks. Moving to nonces is the right next step (Phase-3-wishlist tier) but isn't a
/// release blocker — Razor encoding still handles XSS at the source.</para>
/// <para>jsdelivr.net is whitelisted for the markdown editor (EasyMDE) and CodeMirror; Google Fonts for icons;
/// cdn.discordapp.com for avatar images; data: for inline favicons + bundled icons.</para>
/// </remarks>
public static class SecurityHeadersMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net; " +
        "font-src 'self' data: https://fonts.gstatic.com; " +
        "img-src 'self' data: https://cdn.discordapp.com; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "form-action 'self'";

    private const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), " +
        "microphone=(), payment=(), usb=()";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var headers = ctx.Response.Headers;
                headers.TryAdd("Content-Security-Policy", ContentSecurityPolicy);
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
                headers.TryAdd("Permissions-Policy", PermissionsPolicy);
                // X-Frame-Options is redundant with the CSP frame-ancestors directive but pinned for older
                // browsers that don't honour CSP frame-ancestors.
                headers.TryAdd("X-Frame-Options", "DENY");
                return Task.CompletedTask;
            });

            await next();
        });
    }
}
