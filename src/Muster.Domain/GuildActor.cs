namespace Muster.Domain;

/// <summary>
/// The actor performing a guild-scoped action: a Discord user within a guild. The unit authorization is
/// resolved against (there is no <c>ClaimsPrincipal</c> — the bot has no HTTP principal, and authority is
/// the synced Discord-role snapshot keyed by guild + user). Shared across resource authorizers.
/// </summary>
public readonly record struct GuildActor(ulong GuildId, ulong UserId);
