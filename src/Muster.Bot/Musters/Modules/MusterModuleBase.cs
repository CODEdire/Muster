using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Bot.Modules;

public enum RequiredRole
{
    None,
    Officer,          // legacy umbrella — admin OR OfficerRoleIds. Kept for back-compat.
    QuestManager,
    EconomyManager,   // mint / adjust / bulk-move currency
    EventOfficer,     // create / close event ops
    TrackingManager,  // open/close sessions, configure channels + multipliers
    Auditor,          // read-only observer (audit log, ledger, participation)
    Admin,
}

/// <summary>
/// Base for command modules: enforces the server-only guard and (optionally) role gating, opens a
/// scope, and returns the command result's message. Gating uses <see cref="GuildAuthorizationService"/>,
/// so the guild owner and Discord admins always pass even if the role mapping is empty.
///
/// Replies default to <b>ephemeral</b> (only the invoker sees them); pass <c>ephemeral: false</c> for
/// the few commands whose output is meant to be shared with the channel.
/// </summary>
public abstract class MusterModuleBase(IServiceScopeFactory scopeFactory) : ApplicationCommandModule<ApplicationCommandContext>
{
    protected async Task RunAsync(
        Func<IServiceProvider, ulong, Task<CommandResult>> action,
        RequiredRole required = RequiredRole.None,
        string? auditAction = null,
        bool ephemeral = true)
    {
        // Acknowledge immediately so Discord's 3-second window never closes on slow work (minting, escrow,
        // DB writes). The deferred response is a placeholder we edit with the real result once we're done —
        // every command goes through here, so every command is acked.
        await Context.Interaction.SendResponseAsync(
            InteractionCallback.DeferredMessage(ephemeral ? MessageFlags.Ephemeral : null));

        var content = await ExecuteAsync(action, required, auditAction);

        await Context.Interaction.ModifyResponseAsync(m => m.Content = content);
    }

    private async Task<string> ExecuteAsync(
        Func<IServiceProvider, ulong, Task<CommandResult>> action,
        RequiredRole required,
        string? auditAction)
    {
        if (Context.Guild is not { } guild)
        {
            return "This command can only be used in a server.";
        }

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        if (required != RequiredRole.None)
        {
            var auth = services.GetRequiredService<GuildAuthorizationService>();
            var allowed = required switch
            {
                RequiredRole.Admin => await auth.IsAdminAsync(guild.Id, Context.User.Id),
                RequiredRole.QuestManager => await auth.IsQuestManagerAsync(guild.Id, Context.User.Id),
                RequiredRole.EconomyManager => await auth.IsEconomyManagerAsync(guild.Id, Context.User.Id),
                RequiredRole.EventOfficer => await auth.IsEventOfficerAsync(guild.Id, Context.User.Id),
                RequiredRole.TrackingManager => await auth.IsTrackingManagerAsync(guild.Id, Context.User.Id),
                RequiredRole.Auditor => await auth.IsAuditorAsync(guild.Id, Context.User.Id),
                _ => await auth.IsOfficerAsync(guild.Id, Context.User.Id),
            };

            if (!allowed)
            {
                return required switch
                {
                    RequiredRole.Admin => "You need to be a server admin to use this command.",
                    RequiredRole.QuestManager => "You need to be a quest manager to use this command.",
                    RequiredRole.EconomyManager => "You need economy-manager access to use this command.",
                    RequiredRole.EventOfficer => "You need event-officer access to use this command.",
                    RequiredRole.TrackingManager => "You need tracking-manager access to use this command.",
                    RequiredRole.Auditor => "You need auditor (read-only) access to use this command.",
                    _ => "You need to be an officer to use this command.",
                };
            }
        }

        var result = await action(services, guild.Id);

        // Record successful admin/officer actions to the audit trail (actor = invoking user).
        if (auditAction is not null && !result.IsError)
        {
            await services.GetRequiredService<AuditService>()
                .RecordAsync(guild.Id, Context.User.Id, auditAction, result.Message);
        }

        return result.Message;
    }
}
