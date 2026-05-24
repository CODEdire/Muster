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
// (user-secrets locally, Key Vault in Azure).
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = DiscordAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddDiscord(options =>
    {
        options.ClientId = builder.Configuration["Discord:ClientId"] ?? string.Empty;
        options.ClientSecret = builder.Configuration["Discord:ClientSecret"] ?? string.Empty;
        options.Scope.Add("identify");
        options.Scope.Add("guilds");
        options.SaveTokens = true;
    });

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

// Wolverine.HTTP endpoints (CQRS handlers) live under /api; concrete endpoints land in M5.
app.MapWolverineEndpoints();

app.MapDefaultEndpoints();

app.Run();
