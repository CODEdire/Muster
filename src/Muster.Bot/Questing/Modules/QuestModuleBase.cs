using Microsoft.Extensions.DependencyInjection;
using Muster.Contracts;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Quests;
using NetCord.Services.ApplicationCommands;
using Wolverine;

namespace Muster.Bot.Questing.Modules;

/// <summary>
/// Shared base for the <c>/quest</c> command group and its sub-groups (<c>review</c>, <c>intake</c>). Holds the
/// helpers every quest sub-command needs — parsing the quest id from autocomplete and dispatching a CQRS command
/// through the bus — so the root module and the nested group modules don't each re-implement them.
/// </summary>
public abstract class QuestModuleBase(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    protected static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Parse the quest id (from the autocomplete value), dispatch a CQRS command via the bus, and map the
    /// <see cref="Result"/> to a reply. Auditing happens in the command's audit middleware, not here.</summary>
    protected Task Dispatch<TCommand>(string questRaw, Func<ulong, Guid, TCommand> command, string ok, RequiredRole role = RequiredRole.None)
        where TCommand : class
        => RunAsync(async (sp, guildId) =>
        {
            if (!Guid.TryParse(questRaw, out var questId))
            {
                return CommandResult.Error("That doesn't look like a valid quest id.");
            }

            var result = await sp.GetRequiredService<IMessageBus>().InvokeAsync<Result>(command(guildId, questId));
            return result.ToCommandResult(ok);
        // Acting on an existing quest — reachable whenever quests aren't platform/plan-blocked (CanEnable), so a
        // guild that switched quests off can still wind down in-flight work. The precise per-action rule (e.g. no
        // new claims on an off board) is enforced by the QuestAuthorizer inside the command handler.
        }, role, feature: PlatformFeature.Quests, featureWindDown: true);
}
