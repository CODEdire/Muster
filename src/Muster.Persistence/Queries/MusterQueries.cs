using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;

namespace Muster.Persistence.Queries;

/// <summary>Queries over reaction musters.</summary>
public static class MusterQueries
{
    /// <summary>A muster (with its participants) by the message it's attached to.</summary>
    public static Task<ReactionMuster?> FindMusterByMessageAsync(this MusterDbContext db, ulong messageId, CancellationToken ct = default)
        => db.ReactionMusters.Include(m => m.Participants).FirstOrDefaultAsync(m => m.MessageId == messageId, ct);
}
