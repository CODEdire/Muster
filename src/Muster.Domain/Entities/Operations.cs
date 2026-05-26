namespace Muster.Domain.Entities;

/// <summary>Record of an administrative or configuration action taken via the web UI.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>A registered external connector (e.g. a "Coin" loot system) authorized to call the API.</summary>
public class ApiClient
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Hash of the API key; the raw key is shown once at creation and never stored.</summary>
    public string ApiKeyHash { get; set; } = string.Empty;

    /// <summary>Granted scopes (e.g. read:ledger, write:currency) — what the <i>token</i> may call.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>The Discord user this key <i>acts as</i> (a bot/service account or a designated member, set by an
    /// admin). Actor-bound actions run with this user's identity + guild roles, so what the key can actually do is
    /// the intersection of its scopes and that user's permissions. 0 = unbound (can't perform actor-bound actions).</summary>
    public ulong ActsAsUserId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Links a domain entity (today: a quest) to the Discord message the bot posted for it, so that message can be
/// edited in place as the entity changes state — giving each quest a single live card in the configured channel.
/// Keyed by <see cref="EntityType"/> + <see cref="EntityId"/> so other surfaces (musters, events) can reuse the
/// same board-message machinery later without a new table.
/// </summary>
public class PostedMessage
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>What kind of thing this message renders (e.g. <c>"quest"</c>).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The entity's id (a quest's <see cref="Guid"/>).</summary>
    public Guid EntityId { get; set; }

    /// <summary>The channel the message was posted to (captured so a later channel change can be detected).</summary>
    public ulong ChannelId { get; set; }

    /// <summary>The Discord message id, edited in place on subsequent state changes.</summary>
    public ulong MessageId { get; set; }

    /// <summary>Whether this card has ever been posted to the public board. Once true, a temporary detour into the
    /// mod channel (dispute / final sign-off) returns to the public board on resolution; a never-public quest
    /// (rejected at intake) stays out of public view.</summary>
    public bool EverPublic { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
