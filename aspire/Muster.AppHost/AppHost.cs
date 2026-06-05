using Aspire.Hosting.Azure;
using Microsoft.Extensions.DependencyInjection;
using Muster.AppHost;
using Muster.AppHost.Core;
using Muster.AppHost.PlatformExtensions;

//ConfigInfrastructure => Tags: Environment, Product;

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.Configure<AzureProvisioningOptions>(options =>
{
    options.ProvisioningBuildOptions.InfrastructureResolvers.Add(
        new FixedNameInfrastructureResolver(builder.Environment, builder.Configuration));
});

// Per-environment shapes live in the matching <Topic>Extensions files; AppHost.cs is just the wiring graph.
// The platform graph assembles fluently — each Add* calls the matching AddMuster* factory (a container or
// emulator locally, a provisioned/existing Azure resource in publish) and stashes its reference. Order follows
// the dependency graph: Log Analytics feeds the container env + App Insights; persistence + App Insights feed
// the migration job. Build() snapshots the references web + bot consume.
var platform = builder.AddMuster()
    .AddPersistence()
    .AddLogAnalytics()
    .AddMessaging()
    .AddContainerRegistry()
    .AddContainerEnvironment()
    .AddKeyVault()
    .AddAppConfiguration()
    .AddStorage()
    .AddApplicationInsights()
    .AddSignalR()
    .AddDiscord()
    .AddMigrations()
    .Build();

// Web: Blazor + API. Project process locally; Container App in publish.
var web = builder.AddMusterWeb(platform);

// Dev tunnel: only in run mode (publish has the web's real public Container App URL). Returns null in publish.
var webTunnel = builder.AddMusterDevTunnel(web);

// Bot's Web__BaseUrl: the tunnel's public URL locally; the web's own HTTPS endpoint in publish (Discord
// can reach the live Container App directly with no tunnel in the picture).
var webBaseUrl = webTunnel?.GetEndpoint(web, "https") ?? web.GetEndpoint("https");

// Bot: NetCord gateway + Wolverine handlers. Project process locally; Container App in publish (pinned
// min=max=1 because the gateway protocol is a singleton — see docs/deployment.md "Bot drain").
builder.AddMusterBot(platform, webBaseUrl);

// Cruor mock: local-only test double for the external currency/auction platform the connector pushes to.
// Built from tools/cruor-mock and run as a container in run mode only — no-op in publish (never deployed).
builder.AddMusterCruorMock();

builder.Build().Run();
