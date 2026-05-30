using Muster.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Per-environment shapes live in the matching <Topic>Extensions files; AppHost.cs is just the wiring graph.

// SQL Server: container locally (persistent volume); existing Azure SQL Server bound via AsExisting in publish.
var sql = builder.AddMusterPersistence();
var db = sql.WithMusterDb();

// Service Bus: Microsoft emulator container locally; existing namespace bound via AsExisting in publish.
// Wolverine owns topology via AutoProvision in both envs.
var messaging = builder.AddMusterServiceBus();

// Container Apps Environment (publish only): dedicated to Muster, pulls images from the SHARED registry
// (existing ACR in a shared/platform resource group, bound via AsExisting). Run mode skips this entirely.
_ = builder.AddMusterContainerAppEnvironment();

// Key Vault (secrets) + App Configuration (non-sensitive dynamic config) — both provisioned per
// environment in publish, no-ops in run mode (locals stay on user-secrets + appsettings.Development.json).
var kv = builder.AddMusterKeyVault();
var appconfig = builder.AddMusterAppConfiguration();

// General-purpose Storage account (Azurite locally, provisioned per env in publish). Today only the
// dpkeys container exists (Data Protection key ring, wrapped at rest by the KV RSA key muster-dp-wrap);
// future containers (uploads, exports, …) attach as siblings on the same account.
var storage = builder.AddMusterStorage();
var dpKeys = storage.WithDataProtectionKeys();

// Application Insights (publish only): OTEL traces/metrics/logs sink. Run mode keeps using the Aspire
// Dashboard via the OTLP exporter wired by ServiceDefaults.
var appInsights = builder.AddMusterApplicationInsights();

// Dev-only sidecars: Service Bus emulator UI (peek/send/receive in the Aspire dashboard). Run-mode-gated
// inside the extension; safe to call here unconditionally.
builder.AddMusterServiceBusEmulatorUi(messaging);

// Discord credentials are AppHost parameters → user-secrets locally, Key Vault refs in publish.
var discordToken = builder.AddParameter("discord-token", secret: true);
var discordClientId = builder.AddParameter("discord-clientid", secret: true);
var discordClientSecret = builder.AddParameter("discord-clientsecret", secret: true);

// Run-once schema migration. Local: plain project, bot/web gate on WaitForCompletion. Publish: emitted
// as a Container App Job (manual trigger) — the deploy pipeline starts the job, waits for exit, then
// rolls new bot/web revisions. See docs/deployment.md "Migration job orchestration".
var migrations = builder.AddMusterMigrations(db, appInsights);

// Web: Blazor + API. Project process locally; Container App in publish.
var web = builder.AddMusterWeb(db, messaging, discordToken, discordClientId, discordClientSecret, migrations, kv, appconfig, dpKeys, appInsights);

// Dev tunnel: only in run mode (publish has the web's real public Container App URL). Returns null in publish.
var webTunnel = builder.AddMusterDevTunnel(web);

// Bot's Web__BaseUrl: the tunnel's public URL locally; the web's own HTTPS endpoint in publish (Discord
// can reach the live Container App directly with no tunnel in the picture).
var webBaseUrl = webTunnel?.GetEndpoint(web, "https") ?? web.GetEndpoint("https");

// Bot: NetCord gateway + Wolverine handlers. Project process locally; Container App in publish (pinned
// min=max=1 because the gateway protocol is a singleton — see docs/deployment.md "Bot drain").
builder.AddMusterBot(db, messaging, discordToken, migrations, kv, appconfig, dpKeys, appInsights, webBaseUrl);

builder.Build().Run();
