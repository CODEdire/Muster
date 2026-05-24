using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;

namespace Muster.Infrastructure;

public class MusterDbContext(DbContextOptions<MusterDbContext> options) : DbContext(options)
{
    public DbSet<Guild> Guilds => Set<Guild>();
    public DbSet<DiscordUser> Users => Set<DiscordUser>();
    public DbSet<GuildMember> GuildMembers => Set<GuildMember>();

    public DbSet<TrackingSession> TrackingSessions => Set<TrackingSession>();
    public DbSet<VoiceAttendance> VoiceAttendance => Set<VoiceAttendance>();
    public DbSet<ActivityRecord> ActivityRecords => Set<ActivityRecord>();
    public DbSet<DailyActivityRollup> DailyActivityRollups => Set<DailyActivityRollup>();

    public DbSet<Mission> Missions => Set<Mission>();
    public DbSet<MissionParticipant> MissionParticipants => Set<MissionParticipant>();
    public DbSet<ReactionMuster> ReactionMusters => Set<ReactionMuster>();
    public DbSet<ReactionParticipant> ReactionParticipants => Set<ReactionParticipant>();
    public DbSet<ManualAward> ManualAwards => Set<ManualAward>();

    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Guild>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.OwnsOne(x => x.Settings, s => s.ToJson());
        });

        b.Entity<DiscordUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        b.Entity<GuildMember>(e =>
        {
            e.HasKey(x => new { x.GuildId, x.UserId });
        });

        b.Entity<TrackingSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GuildId, x.Status });
            e.HasMany(x => x.Attendance).WithOne(x => x.TrackingSession!).HasForeignKey(x => x.TrackingSessionId);
        });

        b.Entity<VoiceAttendance>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TrackingSessionId, x.UserId }).IsUnique();
        });

        b.Entity<ActivityRecord>(e =>
        {
            e.HasKey(x => x.Id);
            // Dedupe message activity on gateway redelivery / RESUME.
            e.HasIndex(x => x.SourceMessageId).IsUnique().HasFilter("[SourceMessageId] IS NOT NULL");
        });

        b.Entity<DailyActivityRollup>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GuildId, x.UserId, x.ChannelId, x.Date }).IsUnique();
        });

        b.Entity<Mission>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GuildId, x.Status });
            e.HasMany(x => x.Participants).WithOne(x => x.Mission!).HasForeignKey(x => x.MissionId);
        });

        b.Entity<MissionParticipant>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.MissionId, x.UserId }).IsUnique();
        });

        b.Entity<ReactionMuster>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasMany(x => x.Participants).WithOne(x => x.Muster!).HasForeignKey(x => x.MusterId);
        });

        b.Entity<ReactionParticipant>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.MusterId, x.UserId }).IsUnique();
        });

        b.Entity<ManualAward>(e => e.HasKey(x => x.Id));

        b.Entity<Season>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GuildId, x.Status });
        });

        b.Entity<Currency>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GuildId, x.Code }).IsUnique();
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GuildId, x.UserId, x.CurrencyId, x.SeasonId });
            // Idempotency: a given source produces at most one ledger entry.
            e.HasIndex(x => new { x.SourceType, x.SourceId })
                .IsUnique()
                .HasFilter("[SourceId] IS NOT NULL");
        });

        b.Entity<Wallet>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GuildId, x.UserId, x.CurrencyId, x.SeasonId }).IsUnique();
        });

        b.Entity<AuditLog>(e => e.HasKey(x => x.Id));
        b.Entity<ApiClient>(e => e.HasKey(x => x.Id));
    }
}
