using Muster.Contracts;

namespace Muster.Domain.Entities.Ratings;

/// <summary>
/// A single 1–5★ reputation rating, generic across features. The <see cref="Context"/> + <see cref="SourceId"/>
/// pair points back at the feature row it came from (a shop order today, a quest later) without the reputation
/// layer ever depending on those tables. <see cref="Role"/> is the rated party's side of the deal (Provider =
/// seller/worker, Consumer = buyer/poster), denormalized so "this user's seller reputation" is a single indexed
/// aggregate.
///
/// <para>Blind-mutual: a rating is born <see cref="Hidden"/> and only flips visible (<see cref="RevealedAt"/>)
/// once every party to the source has rated, or the feature's rating window closes — so neither side can react
/// to the other's score. A manager can <see cref="Moderated"/> an abusive one out of aggregates/visibility
/// without deleting the record.</para>
/// </summary>
public class Rating
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    public RatingContext Context { get; set; }

    /// <summary>The feature row this rating is about (e.g. the shop order id). Unique with (Context, RaterId).</summary>
    public Guid SourceId { get; set; }

    /// <summary>Who left the rating.</summary>
    public ulong RaterId { get; set; }

    /// <summary>Who is being rated.</summary>
    public ulong SubjectId { get; set; }

    /// <summary>The subject's role in the transaction (Provider/Consumer) — what the aggregate buckets on.</summary>
    public RatingRole Role { get; set; }

    /// <summary>1–5 stars.</summary>
    public int Stars { get; set; }

    public string? Comment { get; set; }

    /// <summary>Blind until the mutual reveal — excluded from counterparty visibility + aggregates while true.</summary>
    public bool Hidden { get; set; } = true;

    /// <summary>A manager removed this from visibility/aggregates (abuse) without deleting the record.</summary>
    public bool Moderated { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the blind hold lifted (both rated / window closed). Null while still hidden.</summary>
    public DateTimeOffset? RevealedAt { get; set; }

    public static Rating Create(
        ulong guildId, RatingContext context, Guid sourceId, ulong raterId, ulong subjectId, RatingRole role,
        int stars, string? comment)
        => new()
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Context = context,
            SourceId = sourceId,
            RaterId = raterId,
            SubjectId = subjectId,
            Role = role,
            Stars = Math.Clamp(stars, 1, 5),
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            Hidden = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
}
