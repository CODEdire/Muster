using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Commands.Seasons;

namespace Muster.Bot.Seasons;

/// <summary>Discord adapter for season management (admin-only except status). Logic in <see cref="SeasonCommandService"/>.</summary>
public class SeasonModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("season-start", "Start a new season (archives the current one).")]
    public Task StartAsync(
        [SlashCommandParameter(Name = "name", Description = "Season name")] string name)
        => RunAsync(async (sp, guildId) => await sp.GetRequiredService<SeasonCommandService>().StartAsync(guildId, name), RequiredRole.Admin, "season.start");

    [SlashCommand("season-end", "End the current season.")]
    public Task EndAsync()
        => RunAsync(async (sp, guildId) => await sp.GetRequiredService<SeasonCommandService>().EndAsync(guildId), RequiredRole.Admin, "season.end");

    [SlashCommand("season-status", "Show the active season.")]
    public Task StatusAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<SeasonCommandService>().StatusAsync(guildId));
}
