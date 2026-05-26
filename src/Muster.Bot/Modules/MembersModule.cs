using Microsoft.Extensions.DependencyInjection;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services.Membership;
using NetCord.Gateway;
using NetCord.Services.ApplicationCommands;

namespace Muster.Bot.Modules;

/// <summary>
/// Admin tooling for the member roster. <c>/syncmembers</c> pulls the full guild membership from Discord
/// (REST, needs the privileged GuildUsers intent) and upserts it — a manual backfill when the lazy
/// per-event sync hasn't seen everyone yet (e.g. the owner who never triggered a member event).
/// </summary>
public class MembersModule(IServiceScopeFactory scopeFactory) : MusterModuleBase(scopeFactory)
{
    [SlashCommand("syncmembers", "Sync this server's members into Muster (admin).")]
    public Task SyncAsync()
        => RunAsync(async (sp, guildId) =>
        {
            // Resolve the gateway client from the scope (not the ctor) so this module matches the others'
            // single-IServiceScopeFactory shape — NetCord skips a module whose constructor it can't satisfy.
            var client = sp.GetRequiredService<GatewayClient>();
            var snapshots = new List<MemberSnapshot>();
            await foreach (var u in client.Rest.GetGuildUsersAsync(guildId))
            {
                // Bots are synced too (with IsBot) so they can be bound as an API service actor.
                snapshots.Add(new MemberSnapshot(u.Id, u.Username, u.GlobalName, u.AvatarHash, u.Nickname, u.RoleIds, u.IsBot));
            }

            var synced = await sp.GetRequiredService<MemberSyncService>().SyncGuildAsync(guildId, snapshots);
            return CommandResult.Ok($"Synced {synced} member(s) from Discord.");
        }, RequiredRole.Admin, auditAction: "members.sync");
}
