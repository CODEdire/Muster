using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the muster command (admin-only). Logic in <see cref="MusterCommandService"/>.</summary>
public class MusterModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("muster", "Post a reaction check-in that rewards members who react.")]
    public Task<Reply> CreateAsync(
        [SlashCommandParameter(Name = "prompt", Description = "What members are checking in for")] string prompt,
        [SlashCommandParameter(Name = "emoji", Description = "Emoji to react with")] string emoji,
        [SlashCommandParameter(Name = "reward", Description = "Points per check-in")] long reward,
        [SlashCommandParameter(Name = "capacity", Description = "Max reactors (optional)")] long capacity = 0)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<MusterCommandService>()
                .CreateAsync(guildId, Context.Channel.Id, prompt, emoji, reward, capacity > 0 ? (int)capacity : null),
            RequiredRole.Admin,
            auditAction: "muster.create");
}
