using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities;

namespace Muster.Persistence.Configurations;

public class GuildQuestConfiguration : IEntityTypeConfiguration<GuildQuest>
{
    public void Configure(EntityTypeBuilder<GuildQuest> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Status });
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

