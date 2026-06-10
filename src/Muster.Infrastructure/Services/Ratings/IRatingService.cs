using Muster.Contracts;
using Muster.Domain.Entities.Ratings;

namespace Muster.Infrastructure.Services.Ratings;

/// <summary>
/// The generic reputation engine (see <see cref="RatingService"/>). Features call it with their own
/// <see cref="RatingContext"/> + row ids; it owns dedup, the blind-mutual reveal, moderation, and aggregates.
/// </summary>
public interface IRatingService
{
    /// <summary>Record a blind rating. Reveals the whole source once <paramref name="participantCount"/> ratings
    /// exist (0 = never auto-reveal on submit; rely on a window-close call instead).</summary>
    Task<RatingResult> SubmitAsync(
        ulong guildId, RatingContext context, Guid sourceId, ulong raterId, ulong subjectId, RatingRole subjectRole,
        int stars, string? comment, int participantCount, CancellationToken ct = default);

    /// <summary>Lift the blind hold on every rating tied to a source (window close). Returns how many were revealed.</summary>
    Task<int> RevealSourceAsync(RatingContext context, Guid sourceId, CancellationToken ct = default);

    /// <summary>Hide/unhide a rating from visibility + aggregates (manager abuse takedown).</summary>
    Task<bool> ModerateAsync(Rating rating, bool moderated, CancellationToken ct = default);

    /// <summary>A subject's reputation (mean + count) in a role/context, over revealed un-moderated ratings.</summary>
    Task<RatingSummary> GetSummaryAsync(
        ulong guildId, RatingContext context, ulong subjectId, RatingRole role, CancellationToken ct = default);
}
