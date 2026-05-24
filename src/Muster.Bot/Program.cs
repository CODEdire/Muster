using Muster.Infrastructure;
using NetCord.Hosting.Gateway;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddMusterInfrastructure();

// NetCord gateway. The bot token is read from configuration key "Discord:Token"
// (user-secrets locally, Key Vault in Azure). Slash-command modules and gateway event
// handlers (voice attendance, reactions, scheduled events) are added in M2/M3.
builder.Services.AddDiscordGateway();

var host = builder.Build();
host.Run();
