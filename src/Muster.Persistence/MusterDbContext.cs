using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;

namespace Muster.Persistence;

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

    public DbSet<GuildQuest> Quests => Set<GuildQuest>();
    public DbSet<QuestParticipant> QuestParticipants => Set<QuestParticipant>();
    public DbSet<GuildEvent> GuildEvents => Set<GuildEvent>();
    public DbSet<EventAttendee> EventAttendees => Set<EventAttendee>();
    public DbSet<ReactionMuster> ReactionMusters => Set<ReactionMuster>();
    public DbSet<ReactionParticipant> ReactionParticipants => Set<ReactionParticipant>();
    public DbSet<ManualAward> ManualAwards => Set<ManualAward>();

    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<PostedMessage> PostedMessages => Set<PostedMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.ApplyConfigurationsFromAssembly(typeof(MusterDbContext).Assembly);

        // RowVersion is a SQL Server `timestamp` concurrency token — only mapped on SQL Server. The
        // in-memory and SQLite providers used by tests have no equivalent, so they skip it.
        if (Database.IsSqlServer())
        {
            b.Entity<GuildQuest>().Property(x => x.RowVersion).IsRowVersion();
        }
    }
}
