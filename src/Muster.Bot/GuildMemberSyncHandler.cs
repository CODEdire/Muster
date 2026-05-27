using Microsoft.Extensions.Logging;
using Muster.Contracts;
using Muster.Infrastructure.Services.Membership;
using NetCord.Gateway;

namespace Muster.Bot;

/// <summary>
/// Handles a web-triggered <see cref="SyncGuildMembers"/> on the bot host: pulls the guild's full membership from
/// Discord (REST, needs the privileged GuildUsers intent) and upserts it via <see cref="MemberSyncService"/> — the
/// same backfill as <c>/syncmembers</c>, reachable from the admin Members page. Routed over the durable
/// <see cref="WolverineExtensions.MemberSyncQueue"/> so only the bot (which holds the gateway client) runs it.
/// </summary>
public static class GuildMemberSyncHandler
{
    public static async Task Handle(
        SyncGuildMembers command, GatewayClient client, MemberSyncService sync, ILogger<SyncGuildMembers> logger, CancellationToken ct)
    {
        var snapshots = new List<MemberSnapshot>();
        await foreach (var u in client.Rest.GetGuildUsersAsync(command.GuildId))
        {
            // Bots are synced too (with IsBot) so they can be bound as an API service actor.
            snapshots.Add(new MemberSnapshot(u.Id, u.Username, u.GlobalName, u.AvatarHash, u.Nickname, u.RoleIds, u.IsBot));
        }

        var synced = await sync.SyncGuildAsync(command.GuildId, snapshots);
        logger.LogInformation("Web-triggered member sync for guild {Guild}: {Count} member(s)", command.GuildId, synced);
    }
}
