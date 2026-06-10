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
    //
    // ConfigureKeyVault wires Key Vault reference resolution: an App Configuration key-value can be a
    // reference to a Key Vault secret (stored as a {"uri":"https://…/secrets/…"} value) which the provider
    // resolves at load time using the workload's managed identity. The Key Vault Secrets User role is granted
    // by WithReference(kv) on this project in the AppHost, so DefaultAzureCredential picks it up in Azure
    // (and falls back to dev creds locally — though this block only runs when the appconfig source exists).
    builder.AddAzureAppConfiguration("appconfig", configureOptions: options =>
        options
            .ConfigureKeyVault(kv => kv.SetCredential(new Azure.Identity.DefaultAzureCredential()))
            // Load App Configuration feature flags (e.g. "MusterShop") into the FeatureManagement schema so
            // IFeatureManager / the feature gate sees them. Without this only appsettings flags are read.
            .UseFeatureFlags());
}

builder.AddMusterInfrastructure();
builder.AddMusterConnectorProtection();
builder.AddMusterMessaging(HostNames.Web);

// Shop images: the Aspire-published shopimages container client + the upload/serve service. Listing/storefront
// image bytes live in blob storage; the DB only holds the key. Gated on the connection so local-without-AppHost
// dev still boots (the service just has no container to talk to until you run via Aspire).
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("shopimages")))
{
    builder.AddAzureBlobContainerClient("shopimages");
    builder.Services.AddScoped<Muster.Infrastructure.Services.Shops.IShopImageService, Muster.Infrastructure.Services.Shops.ShopImageService>();
}
builder.Services.Configure<Muster.Infrastructure.Services.Shops.ShopImageOptions>(builder.Configuration.GetSection("Shop"));
// EF DbContext + Wolverine runtime readiness checks (tagged "ready") — wires into /health, never /alive.
builder.AddMusterSharedHealthChecks();

// Blazor static SSR by default; specific pages opt into InteractiveServer (a SignalR circuit) via
// @rendermode for live, push-driven views. Static SSR + enhanced nav stays the default everywhere else.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

// The interactive circuit runs over SignalR. In publish we offload it to Azure SignalR (provisioned by the
// AppHost — see SignalRExtensions), which makes scale-out safe (the in-process circuit hub is single-instance).
// UAT/local has no connection string, so the circuit stays in-process — no Azure dependency needed to run.
var signalR = builder.Services.AddSignalR();
// Connection comes from the Aspire-provisioned Azure SignalR resource (ConnectionStrings:signalr, published by
// WithMusterSignalR in the AppHost) in publish; a manually-set Azure:SignalR:ConnectionString (e.g. a KV secret)
// is honored as a fallback. Absent both (local), the circuit stays in-process — no Azure dependency.
var signalRConnection = builder.Configuration.GetConnectionString("signalr")
    ?? builder.Configuration["Azure:SignalR:ConnectionString"];
if (!string.IsNullOrWhiteSpace(signalRConnection))
{
    signalR.AddAzureSignalR(options => options.ConnectionString = signalRConnection);
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

            var avatarHash = Str(root, "avatar");

            // Carry the avatar hash on the user's own identity so the shell can always render their avatar from
            // their token — independent of whether the bot's gateway sync has populated the DB row yet.
            if (avatarHash is not null && ctx.Identity is { } identity)
            {
                identity.AddClaim(new System.Security.Claims.Claim("urn:discord:avatar", avatarHash));
            }

            await using var scope = ctx.HttpContext.RequestServices.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Muster.Infrastructure.Services.Membership.MemberSyncService>()
                .UpsertUserAsync(userId, Str(root, "username") ?? "", Str(root, "global_name"), avatarHash);
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
            // Authenticated web mutations (SSR form posts) — bound per signed-in user so one session can't flood
            // mutation endpoints or the background-job queue. Interactive Blazor clicks ride the /_blazor circuit
            // (not individual HTTP requests), so those are throttled at the action level instead (e.g. one active
            // bulk job per actor in CurrencyBulkService).
            if (HttpMethods.IsPost(http.Request.Method)
                && !http.Request.Path.StartsWithSegments("/_blazor")
                && http.User.GetDiscordUserId() is { } webUserId)
            {
                return RateLimitPartition.GetSlidingWindowLimiter("ui:" + webUserId, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            }

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
    // Local-only return URL (avoid open redirects). Reject backslashes too: browsers normalise '\' to '/', so
    // "/\evil.com" would resolve protocol-relative to evil.com despite passing the single-slash check.
    var target = !string.IsNullOrEmpty(returnUrl)
            && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
            && returnUrl.StartsWith('/')
            && !returnUrl.StartsWith("//")
            && !returnUrl.Contains('\\')
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

// Serves a shop image blob by key. Anonymous on purpose: keys are unguessable random GUIDs and the bytes are
// product imagery meant to be seen — and crucially, Discord fetches embed image/thumbnail/author-icon URLs
// server-side with no auth cookie, so the shop hero/featured cards' images would 401 if this required a login.
app.MapGet("/shop-image/{key}", async (string key, HttpContext http,
    Muster.Infrastructure.Services.Shops.IShopImageService? images, CancellationToken ct) =>
{
    if (images is null)
    {
        return Results.NotFound();
    }

    var found = await images.OpenAsync(key, ct);
    if (found is null)
    {
        return Results.NotFound();
    }

    http.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.Stream(found.Value.Content, found.Value.ContentType);
}).AllowAnonymous();

app.MapDefaultEndpoints();
Console.WriteLine("[MARKER 12] About to call app.Run()");

app.Run();
