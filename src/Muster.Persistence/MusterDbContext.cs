using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;

namespace Muster.Persistence;

public class MusterDbContext(DbContextOptions<MusterDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>Data Protection key ring, persisted to the DB so web/bot/migration hosts share keys (and can
    /// decrypt connector secrets encrypted by another host).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Guild> Guilds => Set<Guild>();
    public DbSet<DiscordUser> Users => Set<DiscordUser>();
    public DbSet<GuildMember> GuildMembers => Set<GuildMember>();
    public DbSet<GuildRole> GuildRoles => Set<GuildRole>();

    public DbSet<TrackingSession> TrackingSessions => Set<TrackingSession>();
    public DbSet<VoiceAttendance> VoiceAttendance => Set<VoiceAttendance>();
    public DbSet<ActivityRecord> ActivityRecords => Set<ActivityRecord>();
    public DbSet<DailyActivityRollup> DailyActivityRollups => Set<DailyActivityRollup>();
    public DbSet<TrackedChannel> TrackedChannels => Set<TrackedChannel>();
    public DbSet<BackgroundVoicePresence> BackgroundVoicePresences => Set<BackgroundVoicePresence>();
    public DbSet<SeasonParticipation> SeasonParticipations => Set<SeasonParticipation>();
    public DbSet<MessageRewardState> MessageRewardStates => Set<MessageRewardState>();
    public DbSet<SessionOptOut> SessionOptOuts => Set<SessionOptOut>();

    public DbSet<GuildQuest> Quests => Set<GuildQuest>();
    public DbSet<QuestParticipant> QuestParticipants => Set<QuestParticipant>();
    public DbSet<GuildEvent> GuildEvents => Set<GuildEvent>();
    public DbSet<EventAttendee> EventAttendees => Set<EventAttendee>();
    public DbSet<ReactionMuster> ReactionMusters => Set<ReactionMuster>();
    public DbSet<ReactionParticipant> ReactionParticipants => Set<ReactionParticipant>();

    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<CurrencyLedgerEntry> CurrencyLedgerEntries => Set<CurrencyLedgerEntry>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<CurrencyBulkBatch> CurrencyBulkBatches => Set<CurrencyBulkBatch>();
    public DbSet<CurrencyWebhook> CurrencyWebhooks => Set<CurrencyWebhook>();

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
