using Microsoft.Extensions.Hosting;
using NetCord.Hosting.Services.ApplicationCommands;

namespace Muster.Bot.Ops;

/// <summary>Per-feature composition for the Ops slice. No DI registrations yet; the slash command
/// module is registered via <see cref="UseOpsModule"/>.</summary>
public static class OpsExtensions
{
    public static IHostApplicationBuilder AddOpsFeature(this IHostApplicationBuilder builder) => builder;

    public static IHost UseOpsModule(this IHost host)
    {
        host.AddApplicationCommandModule<OpModule>();
        return host;
    }
}
