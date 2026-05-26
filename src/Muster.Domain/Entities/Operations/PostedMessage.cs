namespace Muster.Domain.Entities.Operations;

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
