using AspNet.Security.OAuth.Discord;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Muster.Infrastructure;
using Muster.Web.Components;
using Wolverine;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddMusterInfrastructure();
builder.AddMusterConnectorProtection(); // Data Protection for connector secrets (web reads/writes them)
// The web is the live-views consumer: it listens on the session-events and quest-views queues and fans changes
// out to connected Blazor circuits (the bot publishes most session events; quest events come from any origin).
builder.AddMusterMessaging(listenForSessionEvents: true, listenForQuestViews: true);

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

// Per-circuit cache of the viewer's browser time zone, shared by every <LocalTime> for consistent localization.
builder.Services.AddScoped<Muster.Web.BrowserTimeZone>();

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
    .AddCookie();

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

// Required for Wolverine.HTTP endpoint mapping below.
builder.Services.AddWolverineHttp();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
// Antiforgery must run AFTER authentication/authorization so form tokens correlate with the
// signed-in user; otherwise authenticated form posts fail validation.
app.UseAntiforgery();

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

// Public API (/api/v1) and any future CQRS endpoints are Wolverine.HTTP endpoints,
// discovered by assembly scanning.
app.MapWolverineEndpoints();

app.MapDefaultEndpoints();

app.Run();
