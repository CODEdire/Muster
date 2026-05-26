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
builder.AddMusterMessaging();

// Blazor static SSR (no interactive/SignalR render mode).
builder.Services.AddRazorComponents();
builder.Services.AddCascadingAuthenticationState();

// Lists a guild's channels (Discord REST, bot token) for the quest-board channel picker on the settings page.
builder.Services.AddHttpClient<Muster.Web.DiscordChannelLookup>();

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
app.MapRazorComponents<App>();

// Public API (/api/v1) and any future CQRS endpoints are Wolverine.HTTP endpoints,
// discovered by assembly scanning.
app.MapWolverineEndpoints();

app.MapDefaultEndpoints();

app.Run();
