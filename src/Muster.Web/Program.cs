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
builder.AddMusterMessaging();

// Blazor static SSR (no interactive/SignalR render mode).
builder.Services.AddRazorComponents();
builder.Services.AddCascadingAuthenticationState();

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
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

// Discord OAuth login / logout.
app.MapGet("/account/login", (string? returnUrl) =>
    Results.Challenge(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = returnUrl ?? "/guilds" },
        [DiscordAuthenticationDefaults.AuthenticationScheme]));

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
