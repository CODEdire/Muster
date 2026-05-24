using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for event ops. Logic lives in <see cref="OpCommandService"/>.</summary>
public class OpModule(IServiceScopeFactory scopeFactory) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("op-create", "Create an event op members can sign up for.")]
    public async Task<string> CreateAsync(
        [SlashCommandParameter(Name = "name", Description = "Op name")] string name,
        [SlashCommandParameter(Name = "description", Description = "Details")] string description,
        [SlashCommandParameter(Name = "reward", Description = "Points for attendees")] long reward)
        => await RunAsync(commands => commands.CreateAsync(GuildId(), Context.User.Id, name, description, reward));

    [SlashCommand("op-list", "List open event ops.")]
    public async Task<string> ListAsync()
        => await RunAsync(commands => commands.ListAsync(GuildId()));

    [SlashCommand("op-signup", "Sign up for an event op.")]
    public async Task<string> SignUpAsync(
        [SlashCommandParameter(Name = "op", Description = "Op id")] string op)
        => await RunAsync(commands => commands.SignUpAsync(GuildId(), op, Context.User.Id));

    [SlashCommand("op-close", "Close an event op and award attendees.")]
    public async Task<string> CloseAsync(
        [SlashCommandParameter(Name = "op", Description = "Op id")] string op)
        => await RunAsync(commands => commands.CloseAsync(GuildId(), op));

    private ulong GuildId() => Context.Guild!.Id;

    private async Task<string> RunAsync(Func<OpCommandService, Task<CommandResult>> action)
    {
        if (Context.Guild is null)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<OpCommandService>();
        var result = await action(commands);
        return result.Message;
    }
}
