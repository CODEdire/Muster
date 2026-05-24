using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for season management. Logic lives in <see cref="SeasonCommandService"/>.</summary>
public class SeasonModule(IServiceScopeFactory scopeFactory) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("season-start", "Start a new season (archives the current one).")]
    public async Task<string> StartAsync(
        [SlashCommandParameter(Name = "name", Description = "Season name")] string name)
        => await RunAsync(commands => commands.StartAsync(GuildId(), name));

    [SlashCommand("season-end", "End the current season.")]
    public async Task<string> EndAsync()
        => await RunAsync(commands => commands.EndAsync(GuildId()));

    [SlashCommand("season-status", "Show the active season.")]
    public async Task<string> StatusAsync()
        => await RunAsync(commands => commands.StatusAsync(GuildId()));

    private ulong GuildId() => Context.Guild!.Id;

    private async Task<string> RunAsync(Func<SeasonCommandService, Task<CommandResult>> action)
    {
        if (Context.Guild is null)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<SeasonCommandService>();
        var result = await action(commands);
        return result.Message;
    }
}
