using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AspNet.Security.OAuth.Discord;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Muster.Contracts;
using Muster.Infrastructure;
using Muster.Web;
using Muster.Web.Components;
using Wolverine;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// KV (secrets) + AppConfig (dynamic non-secret) as config sources — both publish-only. The Aspire client
// integration reads the connection string Aspire publishes and adds the source so every existing
// builder.Configuration[...] lookup transparently falls through. Gated on connection-string presence so
// local dev (where AppHost's KV/AC extensions no-op) still works on user-secrets + appsettings.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("kv")))
{
    builder.Configuration.AddAzureKeyVaultSecrets("kv");
}
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("appconfig")))
{
    // Aspire client integration — IHostApplicationBuilder extension, NOT IConfigurationManager.
    // The Microsoft.Extensions.Configuration.AzureAppConfiguration extension with the same name on
    // IConfigurationBuilder expects a literal "Endpoint=...;Id=...;Secret=..." connection string and
    // throws "Invalid connection string format" when handed an Aspire connection NAME like "appconfig".
    builder.AddAzureAppConfiguration("appconfig");
}

builder.AddMusterInfrastructure();
builder.AddMusterConnectorProtection();
builder.AddMusterMessaging(HostNames.Web);
// EF DbContext + Wolverine runtime readiness checks (tagged "ready") — wires into /health, never /alive.
builder.AddMusterSharedHealthChecks();

// Blazor static SSR by default; specific pages opt into InteractiveServer (a SignalR circuit) via
// @rendermode for live, push-driven views. Static SSR + enhanced nav stays the default everywhere else.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

// The interactive circuit runs over SignalR. In production we offload it to a pre-provisioned Azure SignalR
// Service (set "Azure:SignalR:ConnectionString" — Key Vault in Azure), which the SDK reads by default; this is
// also what makes scale-out safe (the in-process circuit hub is single-instance). UAT/local has no connection
// string, so the circuit stays in-process — no Azure dependency needed to run.
var signalR = builder.Services.AddSignalR();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Azure:SignalR:ConnectionString"]))
{
    signalR.AddAzureSignalR();
}

// Channel-picker options from the synced GuildChannel roster (no live Discord call).
builder.Services.AddScoped<Muster.Web.GuildChannelOptions>();

// In-process fan-out of live session changes to interactive Blazor circuits (fed by the session-events handler).
builder.Services.AddSingleton<Muster.Web.Live.ISessionUpdateNotifier, Muster.Web.Live.SessionUpdateNotifier>();

// In-process fan-out of live quest changes to interactive Blazor circuits (fed by the quest-views handler).
builder.Services.AddSingleton<Muster.Web.Live.IQuestUpdateNotifier, Muster.Web.Live.QuestUpdateNotifier>();

// In-process fan-out of live muster changes to interactive Blazor circuits (fed by the muster-events handler).
builder.Services.AddSingleton<Muster.Web.Live.IMusterUpdateNotifier, Muster.Web.Live.MusterUpdateNotifier>();

// Per-circuit cache of the viewer's browser time zone, shared by every <LocalTime> for consistent localization.
builder.Services.AddScoped<Muster.Web.BrowserTimeZone>();

// Audit formatter registry — the default catch-all is always present; per-action formatters register themselves
// alongside it as IAuditFormatter. AuditLookups is a per-request entity cache keyed by the rendered tokens.
builder.Services.AddScoped<Muster.Web.Components.Shared.Audit.DefaultAuditFormatter>();
builder.Services.AddScoped<Muster.Web.Components.Shared.Audit.AuditFormatterRegistry>();
builder.Services.AddScoped<Muster.Web.Components.Shared.Audit.AuditLookups>();
// Per-action formatters — pure presentation, one per action family. Register additional ones here as they land.
builder.Services.AddScoped<Muster.Web.Components.Shared.Audit.IAuditFormatter, Muster.Web.Components.Shared.Audit.Formatters.CurrencyMovementFormatter>();
builder.Services.AddScoped<Muster.Web.Components.Shared.Audit.IAuditFormatter, Muster.Web.Components.Shared.Audit.Formatters.ConfigFormatter>();
builder.Services.AddScoped<Muster.Web.Components.Shared.Audit.IAuditFormatter, Muster.Web.Components.Shared.Audit.Formatters.QuestFormatter>();
builder.Services.AddScoped<Muster.Web.Components.Shared.Audit.IAuditFormatter, Muster.Web.Components.Shared.Audit.Formatters.MiscFormatter>();

// Default audit origin for this host = UI. API endpoints + background tasks running inside the web host that
// don't represent a human-driven page click override per-call (e.g. Api endpoints pass AuditOrigin.Api).
builder.Services.AddSingleton<Muster.Infrastructure.Services.Platform.IAuditOriginProvider>(
    _ => new Muster.Infrastructure.Services.Platform.AuditOriginProvider(Muster.Domain.Enums.AuditOrigin.UI));

// Discord OAuth: cookie session, challenge via Discord. Credentials come from configuration
// (user-secrets locally, Key Vault in Azure). Discord is only registered when configured — the
// OAuth handler validates ClientId on every request, so registering it without credentials would
// make the whole site error. Without credentials the app still runs; only login is unavailable.
var discordClientId = builder.Configuration["Discord:ClientId"];
var discordClientSecret = builder.Configuration["Discord:ClientSecret"];
var discordConfigured = !string.IsNullOrWhiteSpace(discordClientId) && !string.IsNullOrWhiteSpace(discordClientSecret);

var authentication = builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        if (discordConfigured)
        {
            options.DefaultChallengeScheme = DiscordAuthenticationDefaults.AuthenticationScheme;
        }
    })
    // Cookie defaults are framework-sourced (HttpOnly + Lax + Secure-on-HTTPS); we explicitly pin SameSite/Secure
    // here for clarity and configure lifetime from settings so ops can rotate without code changes. SameSite=Lax
    // is the minimum for the OAuth redirect flow to complete (Strict drops the auth cookie on the callback hop).
    .AddCookie(options =>
    {
        options.Cookie.HttpOnly = true;                                       // already default; pinned for safety
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;              // HTTPS-only — paired with UseHttpsRedirection
        options.Cookie.SameSite = SameSiteMode.Lax;                           // required for OAuth callback
        options.ExpireTimeSpan = builder.Configuration.GetValue<TimeSpan?>("Auth:CookieExpireTimespan")
                                  ?? TimeSpan.FromDays(14);
        options.SlidingExpiration = builder.Configuration.GetValue<bool?>("Auth:CookieSlidingExpiration") ?? true;
    });

if (discordConfigured)
{
    authentication.AddDiscord(options =>
    {
        options.ClientId = discordClientId!;
        options.ClientSecret = discordClientSecret!;
        options.Scope.Add("identify");
        options.Scope.Add("guilds");
        options.SaveTokens = true;

        // On login, backfill the signed-in user's profile so their name/avatar resolve across the app
        // even before the bot's gateway sync has seen them (per-guild roles still come from the bot).
        options.Events.OnCreatingTicket = async ctx =>
        {
            var root = ctx.User; // the /users/@me response
            if (!root.TryGetProperty("id", out var idEl) || !ulong.TryParse(idEl.GetString(), out var userId))
            {
                return;
            }

            static string? Str(System.Text.Json.JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

            await using var scope = ctx.HttpContext.RequestServices.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Membership.MemberSyncService>()
                .UpsertUserAsync(userId, Str(root, "username") ?? "", Str(root, "global_name"), Str(root, "avatar"));
        };
    });
}

builder.Services.AddAuthorization();
Console.WriteLine("[MARKER 7] Authentication/Authorization added");

// Cap inbound request body size at 256 KB. The public API takes small JSON payloads (mint/spend/transfer
// bodies are ~200 bytes); web form posts are also well under this. Protects against DoS via giant bodies.
// Per-endpoint overrides via [RequestSizeLimit(...)] if a legitimate upload path ever needs more.
builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = 256 * 1024);

// Per-key sliding-window rate limit on /api/v1. Partitioned by SHA-256(X-Api-Key) so a leaked or runaway
// key can't exceed its quota; anonymous (missing key) requests are partitioned by remote IP so unauth scope
// hits are also bounded. 60 req/min is generous for connectors; raise via config if a real workload needs more.
// CORS deliberately NOT configured here — Azure Container Apps owns ingress-level CORS policy (see deployment
// docs). The app stays default-deny so a misconfigured ingress can't accidentally widen the surface.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        if (!http.Request.Path.StartsWithSegments("/api/v1"))
        {
            return RateLimitPartition.GetNoLimiter("non-api");
        }

        var key = http.Request.Headers["X-Api-Key"].FirstOrDefault();
        var partition = string.IsNullOrWhiteSpace(key)
            ? "anon:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            : "key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

        return RateLimitPartition.GetSlidingWindowLimiter(partition, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

// Required for Wolverine.HTTP endpoint mapping below.
builder.Services.AddWolverineHttp();
Console.WriteLine("[MARKER 8] WolverineHttp added");

var app = builder.Build();
Console.WriteLine("[MARKER 9] builder.Build() returned");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
// Security headers (CSP / nosniff / Referrer-Policy / Permissions-Policy / X-Frame-Options) — set early so
// they land on every response including 4xx/5xx that short-circuit later middleware (auth fails, antiforgery
// rejections, rate-limit 429s).
app.UseSecurityHeaders();
app.UseAuthentication();
app.UseAuthorization();
// Antiforgery must run AFTER authentication/authorization so form tokens correlate with the
// signed-in user; otherwise authenticated form posts fail validation.
app.UseAntiforgery();
// Rate limiter — global limiter partitions /api/v1 traffic per-key, returns NoLimit for everything else.
// Place after auth so the partition key (X-Api-Key) is stable for the request.
app.UseRateLimiter();

// Discord OAuth login / logout. Only allow local return URLs (avoid open redirects).
app.MapGet("/account/login", (HttpContext http, string? returnUrl) =>
{
    var target = !string.IsNullOrEmpty(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
        ? returnUrl
        : "/guilds";

    return Results.Challenge(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = target },
        [DiscordAuthenticationDefaults.AuthenticationScheme]);
});

app.MapGet("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
Console.WriteLine("[MARKER 10] Razor components mapped");

// Public API (/api/v1) and any future CQRS endpoints are Wolverine.HTTP endpoints,
// discovered by assembly scanning.
app.MapWolverineEndpoints();
Console.WriteLine("[MARKER 11] WolverineEndpoints mapped");

app.MapDefaultEndpoints();
Console.WriteLine("[MARKER 12] About to call app.Run()");

app.Run();
