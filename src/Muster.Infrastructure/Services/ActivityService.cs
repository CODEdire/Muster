using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

/// <summary>
/// Records stats-only channel activity. Message activity is not rewardable in v1 — it feeds stats
/// and daily rollups. Dedupes on the message id so gateway redelivery never inflates counts.
/// </summary>
public class ActivityService(MusterDbContext db)
{
    public async Task RecordMessageAsync(
        ulong guildId, ulong channelId, ulong userId, ulong messageId, DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        var alreadyRecorded = await db.ActivityRecords.AnyAsync(a => a.SourceMessageId == messageId, ct);
        if (alreadyRecorded)
        {
            return;
        }

        db.ActivityRecords.Add(new ActivityRecord
        {
            GuildId = guildId,
            ChannelId = channelId,
            UserId = userId,
            Type = ActivityType.Message,
            Timestamp = timestamp,
            SourceMessageId = messageId,
        });

        var date = DateOnly.FromDateTime(timestamp.UtcDateTime);
        var rollup = await db.DailyActivityRollups.FirstOrDefaultAsync(
            r => r.GuildId == guildId && r.UserId == userId && r.ChannelId == channelId && r.Date == date, ct);
        if (rollup is null)
        {
            rollup = new DailyActivityRollup
            {
                GuildId = guildId,
                UserId = userId,
                ChannelId = channelId,
                Date = date,
            };
            db.DailyActivityRollups.Add(rollup);
        }

        rollup.MessageCount++;

        await db.SaveChangesAsync(ct);
    }
}
