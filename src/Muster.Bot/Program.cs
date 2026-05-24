using Microsoft.Extensions.DependencyInjection;
using Muster.Bot;
using Muster.Infrastructure;
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
// (user-secrets locally, Key Vault in Azure). Intents avoid privileged ones: message counts
// don't need MessageContent, and members are upserted lazily rather than via GuildUsers.
builder.Services
    .AddDiscordGateway(options =>
    {
        options.Intents =
            GatewayIntents.Guilds
            | GatewayIntents.GuildVoiceStates
            | GatewayIntents.GuildMessages
            | GatewayIntents.GuildMessageReactions
            | GatewayIntents.GuildScheduledEvents;
    })
    .AddApplicationCommands();

// Gateway event handlers in this assembly (e.g. guild onboarding).
builder.Services.AddGatewayHandlers(typeof(Program).Assembly);

// NetCord-backed implementation of the muster publisher abstraction.
builder.Services.AddScoped<IMusterPublisher, NetCordMusterPublisher>();

var host = builder.Build();

// Liveness command plus the participation command modules (award, leaderboard, wallet, track-*).
host.AddSlashCommand("ping", "Check that Muster is responding.", () => "Pong!");
host.AddModules(typeof(Program).Assembly);

host.Run();
