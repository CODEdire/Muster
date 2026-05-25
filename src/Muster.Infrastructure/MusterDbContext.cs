using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Muster.Domain.Entities;

namespace Muster.Infrastructure;

public class MusterDbContext(DbContextOptions<MusterDbContext> options) : DbContext(options)
{
    public DbSet<Guild> Guilds => Set<Guild>();
    public DbSet<DiscordUser> Users => Set<DiscordUser>();
    public DbSet<GuildMember> GuildMembers => Set<GuildMember>();
    public DbSet<GuildRole> GuildRoles => Set<GuildRole>();

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

            // Store settings as a whole-document JSON string (not OwnsOne/ToJson). This avoids EF's
            // per-property JSON_MODIFY updates, which fail on rows whose JSON predates a newly added
            // setting ("property cannot be found on the specified JSON path"). Deserializing a legacy
            // document simply falls back to each property's C# default for any absent key.
            var settingsConverter = new ValueConverter<GuildSettings, string>(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<GuildSettings>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new GuildSettings());
            var settingsComparer = new ValueComparer<GuildSettings>(
                (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null)
                    == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(),
                v => System.Text.Json.JsonSerializer.Deserialize<GuildSettings>(
                    System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null), (System.Text.Json.JsonSerializerOptions?)null)!);
            e.Property(x => x.Settings).HasConversion(settingsConverter, settingsComparer).HasColumnType("nvarchar(max)");
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

        b.Entity<GuildRole>(e =>
        {
            e.HasKey(x => new { x.GuildId, x.RoleId });
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
            if (Database.IsRelational())
            {
                e.Property(x => x.RowVersion).IsRowVersion();
            }
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
