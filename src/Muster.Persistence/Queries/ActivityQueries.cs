using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;

namespace Muster.Persistence.Queries;

/// <summary>Queries over message-activity records and daily rollups.</summary>
public static class ActivityQueries
{
    /// <summary>Whether a message has already been recorded (gateway-redelivery dedupe).</summary>
    public static Task<bool> MessageRecordedAsync(this MusterDbContext db, ulong messageId, CancellationToken ct = default)
        => db.ActivityRecords.AnyAsync(a => a.SourceMessageId == messageId, ct);

    /// <summary>Raw activity records for a guild older than the cutoff (the retention prune sweep deletes these).</summary>
    public static Task<List<ActivityRecord>> StaleActivityRecordsAsync(
        this MusterDbContext db, ulong guildId, DateTimeOffset cutoff, CancellationToken ct = default)
        => db.ActivityRecords.Where(a => a.GuildId == guildId && a.Timestamp < cutoff).ToListAsync(ct);

    /// <summary>The daily rollup row for a user/channel/date, if it exists.</summary>
    public static Task<DailyActivityRollup?> FindDailyRollupAsync(
        this MusterDbContext db, ulong guildId, ulong userId, ulong channelId, DateOnly date, CancellationToken ct = default)
        => db.DailyActivityRollups.FirstOrDefaultAsync(
            r => r.GuildId == guildId && r.UserId == userId && r.ChannelId == channelId && r.Date == date, ct);
}
