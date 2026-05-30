using Microsoft.Extensions.Hosting;
using NetCord.Hosting.Services.ApplicationCommands;

namespace Muster.Bot.Seasons;

/// <summary>Per-feature composition for the Seasons slice. No DI registrations yet; the slash command
/// module is registered via <see cref="UseSeasonsModule"/>.</summary>
public static class SeasonsExtensions
{
    public static IHostApplicationBuilder AddSeasonsFeature(this IHostApplicationBuilder builder) => builder;

    public static IHost UseSeasonsModule(this IHost host)
    {
        host.AddApplicationCommandModule<SeasonModule>();
        return host;
    }
}
