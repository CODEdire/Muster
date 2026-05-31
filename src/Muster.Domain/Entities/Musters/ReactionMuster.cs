using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Musters;

/// <summary>
/// A "muster": a check-in post members opt into with a single Check-In button (a button, not a reaction —
/// the button runs through the command funnel so it can be authorized, audited, gated and acknowledged).
/// The Discord message itself isn't stored here; it lives in <c>PostedMessage</c> (EntityType "muster"), so a
/// muster has no knowledge of channels/messages beyond the channel it was asked to post to.
/// </summary>
public class ReactionMuster
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>The channel the card was asked to post to (the configured muster channel, or an author override).</summary>
    public ulong ChannelId { get; set; }

    /// <summary>Optional short heading shown above the prompt on the card.</summary>
    public string? Title { get; set; }

    public string Prompt { get; set; } = string.Empty;

    /// <summary>Optional decorative emoji shown on the card. No longer load-bearing — check-in is the button,
    /// not a reaction — but kept so a card can carry a themed glyph.</summary>
    public List<string> Emojis { get; set; } = [];

    /// <summary>Optional hard cap: once this many members have checked in, the button is closed to new check-ins.
    /// Ignored for a muster linked to a session (a linked muster rewards everyone who attended, not the first N).</summary>
    public int? Capacity { get; set; }

    /// <summary>Participation POINTS granted (at close) to each member who checked in. 0 = none.</summary>
    public long Points { get; set; }

    /// <summary>Spendable coins granted (at close) to each member who checked in. 0 = none.</summary>
    public long Coins { get; set; }

    /// <summary>Currency the <see cref="Coins"/> are minted in (a spendable currency). Null when Coins = 0.</summary>
    public Guid? CoinCurrencyId { get; set; }

    /// <summary>How long this muster's terminal card lingers before cleanup deletes it — snapshot of the template /
    /// guild default at creation, so cleanup doesn't depend on settings that may have changed since.</summary>
    public int RetentionHours { get; set; } = 48;

    public DateTimeOffset? ExpiresAt { get; set; }

    public MusterStatus Status { get; set; } = MusterStatus.Open;

    public ulong CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public List<ReactionParticipant> Participants { get; set; } = [];

    /// <summary>Tracking sessions this muster gates. When non-empty, each linked session mints its spendable coin
    /// only to attendees who are also in <see cref="Participants"/> (points are unaffected).</summary>
    public List<MusterSessionLink> SessionLinks { get; set; } = [];
}

public class ReactionParticipant
{
    public Guid Id { get; set; }
    public Guid MusterId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>How the check-in was recorded — a member clicking the button, or staff adding them by hand.</summary>
    public MusterParticipantSource Source { get; set; } = MusterParticipantSource.Button;

    public DateTimeOffset CheckedInAt { get; set; }

    public ReactionMuster? Muster { get; set; }
}

/// <summary>Join row: a muster gating a tracking session's coin mint. Many-to-many — a muster may cover several
/// sessions (one check-in covers them all) and a session may carry more than one muster.</summary>
public class MusterSessionLink
{
    public Guid MusterId { get; set; }
    public Guid SessionId { get; set; }

    public ReactionMuster? Muster { get; set; }
}
