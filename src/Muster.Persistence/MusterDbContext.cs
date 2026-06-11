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
    public DbSet<GuildRoleMapping> GuildRoleMappings => Set<GuildRoleMapping>();
    public DbSet<GuildChannel> GuildChannels => Set<GuildChannel>();

    public DbSet<TrackingSession> TrackingSessions => Set<TrackingSession>();
    public DbSet<VoiceAttendance> VoiceAttendance => Set<VoiceAttendance>();
    public DbSet<ActivityRecord> ActivityRecords => Set<ActivityRecord>();
    public DbSet<DailyActivityRollup> DailyActivityRollups => Set<DailyActivityRollup>();
    public DbSet<BackgroundVoicePresence> BackgroundVoicePresences => Set<BackgroundVoicePresence>();
    public DbSet<SeasonParticipation> SeasonParticipations => Set<SeasonParticipation>();
    public DbSet<MessageRewardState> MessageRewardStates => Set<MessageRewardState>();
    public DbSet<SessionOptOut> SessionOptOuts => Set<SessionOptOut>();
    public DbSet<SessionPresenceEvent> SessionPresenceEvents => Set<SessionPresenceEvent>();
    public DbSet<RewardMultiplier> RewardMultipliers => Set<RewardMultiplier>();

    public DbSet<GuildQuest> Quests => Set<GuildQuest>();
    public DbSet<QuestParticipant> QuestParticipants => Set<QuestParticipant>();
    public DbSet<QuestType> QuestTypes => Set<QuestType>();
    public DbSet<GuildQuestSettings> GuildQuestSettings => Set<GuildQuestSettings>();
    public DbSet<GuildEvent> GuildEvents => Set<GuildEvent>();
    public DbSet<EventAttendee> EventAttendees => Set<EventAttendee>();
    public DbSet<ReactionMuster> ReactionMusters => Set<ReactionMuster>();
    public DbSet<ReactionParticipant> ReactionParticipants => Set<ReactionParticipant>();
    public DbSet<MusterSessionLink> MusterSessionLinks => Set<MusterSessionLink>();
    public DbSet<GuildMusterSettings> GuildMusterSettings => Set<GuildMusterSettings>();
    public DbSet<GuildTrackingSettings> GuildTrackingSettings => Set<GuildTrackingSettings>();
    public DbSet<MusterTemplate> MusterTemplates => Set<MusterTemplate>();

    public DbSet<ShopStore> ShopStores => Set<ShopStore>();
    public DbSet<ShopStoreType> ShopStoreTypes => Set<ShopStoreType>();
    public DbSet<ShopCategory> ShopCategories => Set<ShopCategory>();
    public DbSet<ShopListing> ShopListings => Set<ShopListing>();
    public DbSet<ShopListingTag> ShopListingTags => Set<ShopListingTag>();
    public DbSet<ShopOrder> ShopOrders => Set<ShopOrder>();
    public DbSet<GuildShopSettings> GuildShopSettings => Set<GuildShopSettings>();

    public DbSet<Rating> Ratings => Set<Rating>();

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
            b.Entity<ShopStore>().Property(x => x.RowVersion).IsRowVersion();
            b.Entity<ShopListing>().Property(x => x.RowVersion).IsRowVersion();
            b.Entity<ShopOrder>().Property(x => x.RowVersion).IsRowVersion();

            // At most one ACTIVE session may be bound to a given scheduled event — a DB-level guard against the
            // check-then-act race in EnsureForScheduledEventAsync (two near-simultaneous "event started" deliveries).
            // Filtered to active, event-bound rows so closed sessions and manual (null event) sessions are unaffected.
            // SQL-Server-only (filter syntax is provider-specific; tests' in-memory provider can't honor the filter).
            b.Entity<TrackingSession>()
                .HasIndex(x => new { x.GuildId, x.ScheduledEventId })
                .HasFilter("[Status] = 0 AND [ScheduledEventId] IS NOT NULL")
                .IsUnique();
        }
    }
}
