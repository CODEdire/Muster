using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Infrastructure;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

var builder = Host.CreateApplicationBuilder(args);

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
    builder.Configuration.AddAzureAppConfiguration("appconfig");
}

builder.AddMusterInfrastructure();
builder.AddMusterConnectorProtection(); // Data Protection for connector secrets (bot dispatches via connectors)
// EF DbContext + Wolverine runtime readiness checks (tagged "ready"). Bot's gateway-session check is
// registered separately by PlatformExtensions.AddPlatformFeature below.
builder.AddMusterSharedHealthChecks();

// Default audit origin for this host = Bot. Discord command handlers + background bot services use this default;
// long-running automation can override per-call (e.g. AuditOrigin.System for the quest-maintenance sweep).
builder.Services.AddSingleton<Muster.Infrastructure.Services.Platform.IAuditOriginProvider>(
    _ => new Muster.Infrastructure.Services.Platform.AuditOriginProvider(Muster.Domain.Enums.AuditOrigin.Bot));

// Wolverine's conventional routing wires this host as "bot": handlers discovered in the bot assembly +
// Muster.Infrastructure auto-subscribe to the matching topics (quest-lifecycle for the Discord channel
// board, currency-movement for DM receipts + webhooks, sync-guild-members for the roster pull).
builder.AddMusterMessaging(HostNames.Bot);

// NetCord gateway. The bot token is read from configuration key "Discord:Token"
// (user-secrets locally, Key Vault in Azure).
//
// GuildUsers is the privileged "Server Members" intent — required to receive member join/leave/
// update events so the local member tables stay in sync. Enable "Server Members Intent" in the
// Discord Developer Portal (Bot settings). We still avoid the MessageContent privileged intent
// (we only count messages, never read their content).
builder.Services
    .AddDiscordGateway(options =>
    {
        options.Intents =
            GatewayIntents.Guilds
            | GatewayIntents.GuildUsers
            | GatewayIntents.GuildVoiceStates
            | GatewayIntents.GuildMessages
            | GatewayIntents.GuildMessageReactions
            | GatewayIntents.GuildScheduledEvents;
    })
    .AddApplicationCommands()
    // Quest-card buttons + select menus (claim/submit/review/arbitrate/intake). Handlers run the same CQRS
    // commands as slash/web, authorizing the clicker — see QuestInteractionModule.
    .AddComponentInteractions<ButtonInteraction, ButtonInteractionContext>()
    .AddComponentInteractions<StringMenuInteraction, StringMenuInteractionContext>()
    .AddComponentInteractions<ModalInteraction, ModalInteractionContext>();

// Per-feature DI composition. Each Add<Feature>Feature() lives in <Feature>/<Feature>Extensions.cs
// alongside the feature's own code — DI registrations, gateway handlers, autocomplete providers —
// so each slice is self-contained. Order is presentation-only; features don't depend on sequence.
builder
    .AddQuestingFeature()
    .AddEconomyFeature()
    .AddTrackingFeature()
    //.AddMustersFeature()
    .AddMembershipFeature()
    .AddConfigFeature()
    .AddSeasonsFeature()
    //.AddOpsFeature()
    .AddPlatformFeature()
    ;

var host = builder.Build();

// Liveness command — always-on, no feature owns it.
host.AddSlashCommand("ping", "Check that Muster is responding.", () => "Pong!");

// Per-feature post-build module registration: slash command modules + component interaction modules.
// Mirrors ASP.NET's app.UseXxx middleware chain. Platform has no modules so it doesn't appear here.
host.UseQuestingModule()
    .UseEconomyModule()
    .UseTrackingModule()
    //.UseMustersModule()
    .UseMembershipModule()
    .UseConfigModule()
    .UseSeasonsModule()
    //.UseOpsModule()
    ;

host.Run();
