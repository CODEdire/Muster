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
    Officer,
    QuestManager,
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
    protected async Task<Reply> RunAsync(
        Func<IServiceProvider, ulong, Task<CommandResult>> action,
        RequiredRole required = RequiredRole.None,
        string? auditAction = null,
        bool ephemeral = true)
    {
        if (Context.Guild is not { } guild)
        {
            return Message("This command can only be used in a server.", ephemeral);
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
                _ => await auth.IsOfficerAsync(guild.Id, Context.User.Id),
            };

            if (!allowed)
            {
                return Message(required switch
                {
                    RequiredRole.Admin => "You need to be a server admin to use this command.",
                    RequiredRole.QuestManager => "You need to be a quest manager to use this command.",
                    _ => "You need to be an officer to use this command.",
                }, ephemeral);
            }
        }

        var result = await action(services, guild.Id);

        // Record successful admin/officer actions to the audit trail (actor = invoking user).
        if (auditAction is not null && !result.IsError)
        {
            await services.GetRequiredService<AuditService>()
                .RecordAsync(guild.Id, Context.User.Id, auditAction, result.Message);
        }

        return Message(result.Message, ephemeral);
    }

    private static Reply Message(string content, bool ephemeral) =>
        InteractionCallback.Message(new InteractionMessageProperties
        {
            Content = content,
            Flags = ephemeral ? MessageFlags.Ephemeral : null,
        });
}
