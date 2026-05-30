using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Commands.Musters;

namespace Muster.Bot.Musters.Modules;

/// <summary>Discord adapter for the muster command (admin-only). Logic in <see cref="MusterCommandService"/>.</summary>
public class MusterModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("muster", "Post a reaction check-in that rewards members who react.")]
    public Task CreateAsync(
        [SlashCommandParameter(Name = "prompt", Description = "What members are checking in for")] string prompt,
        [SlashCommandParameter(Name = "emoji", Description = "Emoji to react with")] string emoji,
        [SlashCommandParameter(Name = "reward", Description = "Points per check-in")] long reward,
        [SlashCommandParameter(Name = "capacity", Description = "Max reactors (optional)")] long capacity = 0)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<MusterCommandService>()
                .CreateAsync(guildId, Context.Channel.Id, prompt, emoji, reward, capacity > 0 ? (int)capacity : null),
            // Event-tier action — re-tiered from Admin so EventOfficers can run check-ins without admin rights.
            // Admin still passes via lockout-proof bypass.
            RequiredRole.EventOfficer,
            auditAction: "muster.create");
}
