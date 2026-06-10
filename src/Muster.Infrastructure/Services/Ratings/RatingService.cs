using Muster.Contracts;
using Muster.Domain.Entities.Ratings;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Ratings;

/// <summary>Outcome of a rating submission.</summary>
public enum RatingResult
{
    Ok,
    AlreadyRated,
    InvalidStars,
}

/// <summary>A subject's reputation in one role/context: mean stars + how many ratings back it.</summary>
public record RatingSummary(double Avg, int Count);

/// <summary>
/// The feature-agnostic reputation engine: persists blind 1–5★ ratings keyed by (<see cref="RatingContext"/>,
/// source id, rater) and lifts the blind hold mutually. Shop wires it on settled orders today; quests reuse it
/// later by passing their own context + ids — no schema or engine change. Order-state / who-may-rate rules stay
/// with the calling feature; this owns dedup, the blind reveal, moderation, and aggregates.
/// </summary>
public sealed class RatingService(MusterDbContext db) : IRatingService
{
    public async Task<RatingResult> SubmitAsync(
        ulong guildId, RatingContext context, Guid sourceId, ulong raterId, ulong subjectId, RatingRole subjectRole,
        int stars, string? comment, int participantCount, CancellationToken ct = default)
    {
        if (stars is < 1 or > 5)
        {
            return RatingResult.InvalidStars;
        }

        if (await db.FindRatingAsync(context, sourceId, raterId, ct) is not null)
        {
            return RatingResult.AlreadyRated;
        }

        db.Ratings.Add(Rating.Create(guildId, context, sourceId, raterId, subjectId, subjectRole, stars, comment));
        await db.SaveChangesAsync(ct);

        // Mutual reveal: once everyone party to the source has rated, lift the blind hold on all of them at once.
        if (participantCount > 0 && await db.CountRatingsForSourceAsync(context, sourceId, ct) >= participantCount)
        {
            await RevealSourceAsync(context, sourceId, ct);
        }

        return RatingResult.Ok;
    }

    public async Task<int> RevealSourceAsync(RatingContext context, Guid sourceId, CancellationToken ct = default)
    {
        var ratings = await db.ListRatingsForSourceAsync(context, sourceId, ct);
        var now = DateTimeOffset.UtcNow;
        var revealed = 0;
        foreach (var r in ratings.Where(r => r.Hidden))
        {
            r.Hidden = false;
            r.RevealedAt = now;
            revealed++;
        }

        if (revealed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return revealed;
    }

    public async Task<bool> ModerateAsync(Rating rating, bool moderated, CancellationToken ct = default)
    {
        if (rating.Moderated == moderated)
        {
            return true;
        }

        rating.Moderated = moderated;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<RatingSummary> GetSummaryAsync(
        ulong guildId, RatingContext context, ulong subjectId, RatingRole role, CancellationToken ct = default)
    {
        var (avg, count) = await db.RatingSummaryAsync(guildId, context, subjectId, role, ct);
        return new RatingSummary(avg, count);
    }
}
