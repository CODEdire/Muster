using Microsoft.Extensions.Hosting;
using NetCord.Hosting.Services.ApplicationCommands;

namespace Muster.Bot.Config;

/// <summary>Per-feature composition for the Config slice. No DI registrations yet; the slash command
/// module is registered via <see cref="UseConfigModule"/>.</summary>
public static class ConfigExtensions
{
    public static IHostApplicationBuilder AddConfigFeature(this IHostApplicationBuilder builder) => builder;

    public static IHost UseConfigModule(this IHost host)
    {
        host.AddApplicationCommandModule<ConfigModule>();
        return host;
    }
}
