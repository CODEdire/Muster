namespace Muster.Contracts;

/// <summary>Ask the bot to pull this guild's full membership from Discord (REST, needs the privileged GuildUsers
/// intent) and upsert it — the web equivalent of <c>/syncmembers</c>. Routed to the bot host (only it holds the
/// gateway client); fire-and-forget, so the admin refreshes the roster a moment later to see the result.</summary>
public record SyncGuildMembers(ulong GuildId);
