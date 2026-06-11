using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities;
using Muster.Domain.Entities.Guilds;

namespace Muster.Persistence.Configurations;

public class GuildQuestConfiguration : IEntityTypeConfiguration<GuildQuest>
{
    public void Configure(EntityTypeBuilder<GuildQuest> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Status });
        e.HasIndex(x => x.QuestTypeId);
        e.HasMany(x => x.Participants).WithOne(x => x.Quest!).HasForeignKey(x => x.QuestId);
        // RowVersion is configured as a concurrency token in MusterDbContext, gated on a relational provider
        // (the in-memory provider used by tests can't support IsRowVersion()).
    }
}

public class QuestParticipantConfiguration : IEntityTypeConfiguration<QuestParticipant>
{
    public void Configure(EntityTypeBuilder<QuestParticipant> e)
    {
        e.HasKey(x => x.Id);
        // Not unique: a member may hold several participations in one quest when it allows repeat completions.
        e.HasIndex(x => new { x.QuestId, x.UserId });
    }
}

public class QuestTypeConfiguration : IEntityTypeConfiguration<QuestType>
{
    public void Configure(EntityTypeBuilder<QuestType> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Name }).IsUnique();
        e.Property(x => x.Name).HasMaxLength(40);
        e.Property(x => x.Icon).HasMaxLength(32);
    }
}

public class GuildQuestSettingsConfiguration : IEntityTypeConfiguration<GuildQuestSettings>
{
    public void Configure(EntityTypeBuilder<GuildQuestSettings> e)
    {
        // 1:1 with Guild, keyed + FK on GuildId (delete the guild → delete its quest settings).
        e.HasKey(x => x.GuildId);
        e.Property(x => x.GuildId).ValueGeneratedNever();
        e.HasOne<Guild>().WithOne().HasForeignKey<GuildQuestSettings>(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
    }
}

