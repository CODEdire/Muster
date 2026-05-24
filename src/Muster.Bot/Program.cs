using Muster.Infrastructure;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddMusterInfrastructure();

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

var host = builder.Build();

// Liveness command. The full participation command set (track/quest/op/muster/award/...) lands
// in M3 as application-command modules.
host.AddSlashCommand("ping", "Check that Muster is responding.", () => "Pong!");

host.Run();
