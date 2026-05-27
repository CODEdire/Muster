namespace Muster.Domain.Entities.Musters;

/// <summary>A reaction "muster" message: react to check in. May carry multiple emoji options.</summary>
public class ReactionMuster
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    /// <summary>Emoji that count toward this muster (each may map to a distinct response).</summary>
    public List<string> Emojis { get; set; } = [];

    /// <summary>Optional cap: only the first N reactors are rewarded.</summary>
    public int? Capacity { get; set; }

    public Guid CurrencyId { get; set; }
    public long RewardAmount { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public List<ReactionParticipant> Participants { get; set; } = [];
}

public class ReactionParticipant
{
    public Guid Id { get; set; }
    public Guid MusterId { get; set; }
    public ulong UserId { get; set; }
    public string Emoji { get; set; } = string.Empty;
    public DateTimeOffset ReactedAt { get; set; }

    public ReactionMuster? Muster { get; set; }
}
