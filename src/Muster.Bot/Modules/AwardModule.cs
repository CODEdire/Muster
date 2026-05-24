using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>Discord adapter for the award command (admin-only). Logic lives in <see cref="AwardCommandService"/>.</summary>
public class AwardModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("award", "Award points to a member for an off-platform contribution.")]
    public Task<string> AwardAsync(
        [SlashCommandParameter(Name = "member", Description = "Member to award")] User member,
        [SlashCommandParameter(Name = "amount", Description = "Points to award")] long amount,
        [SlashCommandParameter(Name = "reason", Description = "Why they're being awarded")] string reason)
        => RunAsync(
            (sp, guildId) => sp.GetRequiredService<AwardCommandService>()
                .AwardPointsAsync(guildId, Context.User.Id, member.Id, amount, reason),
            RequiredRole.Admin);
}
