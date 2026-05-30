using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Muster.Infrastructure.Commands.Musters;
using Muster.Infrastructure.Discord;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;

namespace Muster.Bot.Musters;

/// <summary>
/// Per-feature composition for the event-musters slice. <see cref="AddMustersFeature"/> registers the
/// publisher abstraction (NetCord-backed), the command service that uses it, and the reaction handler
/// that watches for muster check-ins; <see cref="UseMustersModule"/> registers the slash command module.
/// </summary>
public static class MustersExtensions
{
    public static IHostApplicationBuilder AddMustersFeature(this IHostApplicationBuilder builder)
    {
        // NetCord-backed implementation of the muster publisher abstraction (Infrastructure provides
        // the contract; only the bot has the gateway/REST client to actually post a muster).
        builder.Services.AddScoped<IMusterPublisher, NetCordMusterPublisher>();
        builder.Services.AddScoped<MusterCommandService>();

        // Discord gateway handler for reaction-based muster check-ins.
        builder.Services.AddGatewayHandler<MusterReactionHandler>();

        return builder;
    }

    public static IHost UseMustersModule(this IHost host)
    {
        host.AddApplicationCommandModule<MusterModule>();
        return host;
    }
}
