namespace Muster.Contracts;

// Muster (reaction check-in) message contracts. Every surface — slash command, Check-In button, web admin —
// runs through these IGuildCommand records so authorization + auditing happen in one place (the handler + audit
// middleware), exactly like the quest commands. Check-in is a button click dispatched as CheckInMuster with the
// clicker as ActorId; lifecycle actions (create/close/link/edit roster) are staff commands.

/// <summary>Create a muster and post its card. Rewards resolve as <b>template → custom → guild defaults</b>:
/// <paramref name="TemplateId"/> picks a preset; <paramref name="Points"/>/<paramref name="Coins"/>/
/// <paramref name="CoinCurrencyId"/> are a Tracking Manager's custom values (and may override a template). A
/// template-only Muster Creator must supply a <paramref name="TemplateId"/> and can't override. Null reward fields
/// fall back to the template, then the guild defaults. <paramref name="SessionId"/> links to a session in one step.
/// <paramref name="CheckInCreator"/> null = use the guild default (auto-check-in the creator); true/false overrides it.
/// Returns the new muster id.</summary>
public record CreateMuster(
    ulong GuildId,
    ulong ActorId,
    ulong ChannelId,
    string? Title,
    string Prompt,
    Guid? TemplateId,
    long? Points,
    long? Coins,
    Guid? CoinCurrencyId,
    int? Capacity,
    DateTimeOffset? ExpiresAt,
    Guid? SessionId,
    bool? CheckInCreator = null) : IGuildCommand;

/// <summary>Record a member's check-in. <see cref="IGuildCommand.ActorId"/> is the member checking in (the button
/// clicker), so no manager gate — eligibility (participant role + open/!full/!expired) is enforced in the handler.</summary>
public record CheckInMuster(ulong GuildId, ulong ActorId, Guid MusterId) : IGuildCommand;

/// <summary>Staff adds a member to a muster by hand (web/command). Mints the reward like a normal check-in,
/// idempotent on the same source key, and bypasses the capacity cap (staff override).</summary>
public record AddMusterParticipant(ulong GuildId, ulong ActorId, Guid MusterId, ulong UserId) : IGuildCommand;

/// <summary>Staff removes a member from a muster's roster. Does NOT reverse any already-minted reward — a paid coin
/// is undone via the EconomyManager currency-adjust path, not here.</summary>
public record RemoveMusterParticipant(ulong GuildId, ulong ActorId, Guid MusterId, ulong UserId) : IGuildCommand;

/// <summary>Close a muster: stop accepting check-ins and render its card terminal.</summary>
public record CloseMuster(ulong GuildId, ulong ActorId, Guid MusterId) : IGuildCommand;

/// <summary>Link a muster to a tracking session so that session's coin mint is gated on muster check-in.</summary>
public record LinkMusterToSession(ulong GuildId, ulong ActorId, Guid MusterId, Guid SessionId) : IGuildCommand;

/// <summary>Remove a muster↔session link.</summary>
public record UnlinkMusterFromSession(ulong GuildId, ulong ActorId, Guid MusterId, Guid SessionId) : IGuildCommand;

/// <summary>Set how a tracking session's spendable-coin mint is gated by its linked muster(s) at close.</summary>
public record SetSessionCoinGate(ulong GuildId, ulong ActorId, Guid SessionId, SessionCoinGate Gate) : IGuildCommand;

/// <summary>What changed on a muster, so the bot can (re)render or remove its card. Published by the handlers after
/// the DB write commits — the bot's notification handler edits the Discord card in place (the quest-board pattern).</summary>
public enum MusterChangeKind
{
    Created = 0,
    CheckedIn = 1,           // a participant joined (or was added)
    ParticipantsChanged = 2, // a participant was removed
    Closed = 3,
}

/// <summary>Muster lifecycle notification (bot renders the card off this).</summary>
public record MusterChanged(ulong GuildId, Guid MusterId, MusterChangeKind Kind);

/// <summary>Ask the bot to delete a muster's Discord card now and drop its board link, without touching the muster
/// data — the manual "remove from channel" action. Routed to the bot (it owns the gateway client).</summary>
public record RemoveMusterCard(ulong GuildId, Guid MusterId);
