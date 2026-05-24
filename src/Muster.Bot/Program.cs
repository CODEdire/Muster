using Microsoft.Extensions.DependencyInjection;
using Muster.Bot;
using Muster.Infrastructure;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Discord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Services.ApplicationCommands;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddMusterInfrastructure();
builder.AddMusterMessaging();

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
    .AddApplicationCommands();

// Gateway event handlers in this assembly (e.g. guild onboarding).
builder.Services.AddGatewayHandlers(typeof(Program).Assembly);

// NetCord-backed implementation of the muster publisher abstraction, plus the muster command
// service that depends on it (a bot-only concern — the web doesn't post muster messages).
builder.Services.AddScoped<IMusterPublisher, NetCordMusterPublisher>();
builder.Services.AddScoped<MusterCommandService>();

// Autocomplete providers for slash-command parameters (currency codes, quest ids).
builder.Services.AddTransient<Muster.Bot.Autocomplete.CurrencyAutocompleteProvider>();
builder.Services.AddTransient<Muster.Bot.Autocomplete.QuestAutocompleteProvider>();

var host = builder.Build();

// Liveness command plus the participation command modules (award, leaderboard, wallet, track-*).
host.AddSlashCommand("ping", "Check that Muster is responding.", () => "Pong!");
host.AddModules(typeof(Program).Assembly);

host.Run();
