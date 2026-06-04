using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities;
using Muster.Domain.Entities.Guilds;

namespace Muster.Persistence.Configurations;

public class GuildTrackingSettingsConfiguration : IEntityTypeConfiguration<GuildTrackingSettings>
{
    public void Configure(EntityTypeBuilder<GuildTrackingSettings> e)
    {
        // 1:1 with Guild, keyed + FK on GuildId (delete the guild → delete its tracking settings).
        e.HasKey(x => x.GuildId);
        e.Property(x => x.GuildId).ValueGeneratedNever();
        e.HasOne<Guild>().WithOne().HasForeignKey<GuildTrackingSettings>(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
        e.Property(x => x.MultiplierCap).HasPrecision(6, 3);
        e.Property(x => x.SessionCoinCurrencyCode).HasMaxLength(16);
    }
}

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

public class BackgroundVoicePresenceConfiguration : IEntityTypeConfiguration<BackgroundVoicePresence>
{
    public void Configure(EntityTypeBuilder<BackgroundVoicePresence> e)
    {
        e.HasKey(x => x.Id);
        // One accrual row per member per channel; reconcile looks it up by this key.
        e.HasIndex(x => new { x.GuildId, x.UserId, x.ChannelId }).IsUnique();
    }
}

public class SeasonParticipationConfiguration : IEntityTypeConfiguration<SeasonParticipation>
{
    public void Configure(EntityTypeBuilder<SeasonParticipation> e)
    {
        e.HasKey(x => x.Id);
        // One accumulator per member per season.
        e.HasIndex(x => new { x.GuildId, x.UserId, x.SeasonId }).IsUnique();
    }
}

public class MessageRewardStateConfiguration : IEntityTypeConfiguration<MessageRewardState>
{
    public void Configure(EntityTypeBuilder<MessageRewardState> e)
    {
        e.HasKey(x => x.Id);
        // One anti-spam state per member per channel.
        e.HasIndex(x => new { x.GuildId, x.UserId, x.ChannelId }).IsUnique();
    }
}

public class SessionOptOutConfiguration : IEntityTypeConfiguration<SessionOptOut>
{
    public void Configure(EntityTypeBuilder<SessionOptOut> e)
    {
        e.HasKey(x => x.Id);
        // One opt-out per member per session.
        e.HasIndex(x => new { x.SessionId, x.UserId }).IsUnique();
    }
}

public class SessionPresenceEventConfiguration : IEntityTypeConfiguration<SessionPresenceEvent>
{
    public void Configure(EntityTypeBuilder<SessionPresenceEvent> e)
    {
        e.HasKey(x => x.Id);
        // Timeline + audit read the stream for one session in chronological order.
        e.HasIndex(x => new { x.SessionId, x.AtUtc });
        e.Property(x => x.Reason).HasMaxLength(64);
    }
}

public class RewardMultiplierConfiguration : IEntityTypeConfiguration<RewardMultiplier>
{
    public void Configure(EntityTypeBuilder<RewardMultiplier> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Enabled });
        e.Property(x => x.Name).HasMaxLength(100);
        e.Property(x => x.Factor).HasPrecision(6, 3);
    }
}
