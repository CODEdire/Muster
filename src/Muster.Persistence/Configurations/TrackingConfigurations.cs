using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities;

namespace Muster.Persistence.Configurations;

public class TrackingSessionConfiguration : IEntityTypeConfiguration<TrackingSession>
{
    public void Configure(EntityTypeBuilder<TrackingSession> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Status });
        e.HasMany(x => x.Attendance).WithOne(x => x.TrackingSession!).HasForeignKey(x => x.TrackingSessionId);
    }
}

public class VoiceAttendanceConfiguration : IEntityTypeConfiguration<VoiceAttendance>
{
    public void Configure(EntityTypeBuilder<VoiceAttendance> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.TrackingSessionId, x.UserId }).IsUnique();
    }
}

public class ActivityRecordConfiguration : IEntityTypeConfiguration<ActivityRecord>
{
    public void Configure(EntityTypeBuilder<ActivityRecord> e)
    {
        e.HasKey(x => x.Id);
        // Dedupe message activity on gateway redelivery / RESUME.
        e.HasIndex(x => x.SourceMessageId).IsUnique().HasFilter("[SourceMessageId] IS NOT NULL");
    }
}

public class DailyActivityRollupConfiguration : IEntityTypeConfiguration<DailyActivityRollup>
{
    public void Configure(EntityTypeBuilder<DailyActivityRollup> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.UserId, x.ChannelId, x.Date }).IsUnique();
    }
}
