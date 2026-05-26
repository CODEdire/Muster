using Microsoft.Extensions.DependencyInjection;
using Muster.Bot;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Discord;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;
using Muster.Infrastructure.Commands.Musters;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddMusterInfrastructure();
builder.AddMusterConnectorProtection(); // Data Protection for connector secrets (bot dispatches via connectors)

// The bot is the only host that listens on the quest-board and currency-events queues: it renders/updates the
// Discord channel board in response to quest lifecycle events, and DMs currency receipts (grants/staff actions)
// to recipients — both published from any host.
builder.AddMusterMessaging(listenForQuestBoard: true, listenForCurrencyEvents: true);

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

// Gateway event handlers in this assembly (e.g. guild onboarding).
builder.Services.AddGatewayHandlers(typeof(Program).Assembly);

// Publishes the periodic quest reconciliation tick (activate scheduled / expire past-deadline).
// The tick is handled on the cluster leader only (see WolverineExtensions), so this is safe to run
// here even if the bot scales out.
builder.Services.AddHostedService<Muster.Infrastructure.Messaging.QuestSweepScheduler>();

// Prunes completed quest cards from the channel board after the guild's retention window (bot-only — needs REST).
builder.Services.AddHostedService<Muster.Bot.QuestBoardCleanupScheduler>();

// DMs "closes soon" deadline nudges to active workers / a taker-less bounty owner (bot-only — needs DM REST).
builder.Services.AddHostedService<Muster.Bot.QuestReminderScheduler>();

// Periodically reconciles External/Hybrid balances from their connectors (per-currency sync interval).
builder.Services.AddHostedService<Muster.Bot.CurrencyBalanceSyncScheduler>();

// Daily: compacts ledger history beyond each guild's LedgerRetentionDays into carry-forward checkpoints.
builder.Services.AddHostedService<Muster.Bot.LedgerPruneScheduler>();

// NetCord-backed implementation of the muster publisher abstraction, plus the muster command
// service that depends on it (a bot-only concern — the web doesn't post muster messages).
builder.Services.AddScoped<IMusterPublisher, NetCordMusterPublisher>();
builder.Services.AddScoped<MusterCommandService>();

// Autocomplete providers for slash-command parameters (currency codes, quest ids).
builder.Services.AddTransient<Muster.Bot.Autocomplete.CurrencyAutocompleteProvider>();
builder.Services.AddTransient<Muster.Bot.Autocomplete.QuestCurrencyAutocompleteProvider>();
builder.Services.AddTransient<Muster.Bot.Autocomplete.QuestAutocompleteProvider>();
builder.Services.AddTransient<Muster.Bot.Autocomplete.TimezoneAutocompleteProvider>();

var host = builder.Build();

// Liveness command plus the participation command modules (award, leaderboard, wallet, track-*).
host.AddSlashCommand("ping", "Check that Muster is responding.", () => "Pong!");
host.AddModules(typeof(Program).Assembly);

host.Run();
