using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for event ops. Logic lives in <see cref="OpCommandService"/>.</summary>
public class OpModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("op-create", "Create an event op members can sign up for.")]
    public Task<string> CreateAsync(
        [SlashCommandParameter(Name = "name", Description = "Op name")] string name,
        [SlashCommandParameter(Name = "description", Description = "Details")] string description,
        [SlashCommandParameter(Name = "reward", Description = "Points for attendees")] long reward)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<OpCommandService>().CreateAsync(guildId, Context.User.Id, name, description, reward),
            RequiredRole.Officer,
            auditAction: "op.create");

    [SlashCommand("op-list", "List open event ops.")]
    public Task<string> ListAsync()
        => RunAsync((sp, guildId) => sp.GetRequiredService<OpCommandService>().ListAsync(guildId));

    [SlashCommand("op-signup", "Sign up for an event op.")]
    public Task<string> SignUpAsync(
        [SlashCommandParameter(Name = "op", Description = "Op id")] string op)
        => RunAsync((sp, guildId) => sp.GetRequiredService<OpCommandService>().SignUpAsync(guildId, op, Context.User.Id));

    [SlashCommand("op-close", "Close an event op and award attendees.")]
    public Task<string> CloseAsync(
        [SlashCommandParameter(Name = "op", Description = "Op id")] string op)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<OpCommandService>().CloseAsync(guildId, op),
            RequiredRole.Officer,
            auditAction: "op.close");
}
