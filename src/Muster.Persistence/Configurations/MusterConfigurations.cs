using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Entities.Musters;

namespace Muster.Persistence.Configurations;

public class GuildMusterSettingsConfiguration : IEntityTypeConfiguration<GuildMusterSettings>
{
    public void Configure(EntityTypeBuilder<GuildMusterSettings> e)
    {
        // 1:1 with Guild, keyed + FK on GuildId (delete the guild → delete its muster settings).
        e.HasKey(x => x.GuildId);
        e.Property(x => x.GuildId).ValueGeneratedNever();
        e.HasOne<Guild>().WithOne().HasForeignKey<GuildMusterSettings>(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
        // AllowedChannelIds is a primitive collection — EF maps it to a JSON column (a small, whole-read list).
    }
}

public class ReactionMusterConfiguration : IEntityTypeConfiguration<ReactionMuster>
{
    public void Configure(EntityTypeBuilder<ReactionMuster> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Status });
        e.HasMany(x => x.Participants).WithOne(x => x.Muster!).HasForeignKey(x => x.MusterId);
        e.HasMany(x => x.SessionLinks).WithOne(x => x.Muster!).HasForeignKey(x => x.MusterId);
    }
}

public class ReactionParticipantConfiguration : IEntityTypeConfiguration<ReactionParticipant>
{
    public void Configure(EntityTypeBuilder<ReactionParticipant> e)
    {
        e.HasKey(x => x.Id);
        // One check-in per member per muster (the prod backstop for a button double-click race; the in-memory
        // provider used by tests doesn't enforce this, so the service also guards in memory).
        e.HasIndex(x => new { x.MusterId, x.UserId }).IsUnique();
    }
}

public class MusterSessionLinkConfiguration : IEntityTypeConfiguration<MusterSessionLink>
{
    public void Configure(EntityTypeBuilder<MusterSessionLink> e)
    {
        e.HasKey(x => new { x.MusterId, x.SessionId });
        e.HasIndex(x => x.SessionId); // close-time lookup: which musters gate this session
    }
}
