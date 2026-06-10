using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Entities.Ratings;

namespace Muster.Persistence.Queries;

/// <summary>
/// Queries over the generic reputation table. Feature-agnostic: callers pass a <see cref="RatingContext"/> + the
/// feature's row id (<c>sourceId</c>) / a subject. Aggregates count only revealed, un-moderated ratings so blind
/// holds and abuse takedowns never skew a reputation.
/// </summary>
public static class RatingQueries
{
    public static Task<Rating?> FindRatingAsync(this MusterDbContext db, RatingContext context, Guid sourceId, ulong raterId, CancellationToken ct = default)
        => db.Ratings.FirstOrDefaultAsync(r => r.Context == context && r.SourceId == sourceId && r.RaterId == raterId, ct);

    public static Task<Rating?> FindRatingByIdAsync(this MusterDbContext db, ulong guildId, Guid id, CancellationToken ct = default)
        => db.Ratings.FirstOrDefaultAsync(r => r.Id == id && r.GuildId == guildId, ct);

    /// <summary>All ratings tied to one source (both parties of an order), tracked so the reveal can flip them.</summary>
    public static Task<List<Rating>> ListRatingsForSourceAsync(this MusterDbContext db, RatingContext context, Guid sourceId, CancellationToken ct = default)
        => db.Ratings.Where(r => r.Context == context && r.SourceId == sourceId).ToListAsync(ct);

    /// <summary>Count of (un-moderated) ratings on a source — drives "everyone's rated → reveal".</summary>
    public static Task<int> CountRatingsForSourceAsync(this MusterDbContext db, RatingContext context, Guid sourceId, CancellationToken ct = default)
        => db.Ratings.CountAsync(r => r.Context == context && r.SourceId == sourceId && !r.Moderated, ct);

    /// <summary>A subject's reputation in a role: average stars + count over revealed, un-moderated ratings.</summary>
    public static async Task<(double Avg, int Count)> RatingSummaryAsync(
        this MusterDbContext db, ulong guildId, RatingContext context, ulong subjectId, RatingRole role, CancellationToken ct = default)
    {
        var rows = await db.Ratings.AsNoTracking()
            .Where(r => r.GuildId == guildId && r.Context == context && r.SubjectId == subjectId
                && r.Role == role && !r.Hidden && !r.Moderated)
            .Select(r => r.Stars).ToListAsync(ct);
        return rows.Count == 0 ? (0d, 0) : (rows.Average(), rows.Count);
    }
}
