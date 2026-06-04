using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities;
using Muster.Domain.Entities.Members;

namespace Muster.Persistence.Configurations;

public class GuildConfiguration : IEntityTypeConfiguration<Guild>
{
    public void Configure(EntityTypeBuilder<Guild> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.OwnsOne(x => x.Settings, s =>
        {
            s.ToJson();
            s.OwnsOne(x => x.Quests); // nested within the same settings JSON document
        });
    }
}

public class DiscordUserConfiguration : IEntityTypeConfiguration<DiscordUser>
{
    public void Configure(EntityTypeBuilder<DiscordUser> e)
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
    }
}

public class GuildMemberConfiguration : IEntityTypeConfiguration<GuildMember>
{
    public void Configure(EntityTypeBuilder<GuildMember> e)
        => e.HasKey(x => new { x.GuildId, x.UserId });
}

public class GuildRoleConfiguration : IEntityTypeConfiguration<GuildRole>
{
    public void Configure(EntityTypeBuilder<GuildRole> e)
        => e.HasKey(x => new { x.GuildId, x.RoleId });
}

public class GuildRoleMappingConfiguration : IEntityTypeConfiguration<GuildRoleMapping>
{
    public void Configure(EntityTypeBuilder<GuildRoleMapping> e)
    {
        e.HasKey(x => new { x.GuildId, x.RoleId });
        // GuildRoleTier is a [Flags] byte enum → stored as tinyint.
        e.Property(x => x.Tiers);
        // Cascade with the guild; no FK to GuildRole (a mapping may outlive an unsynced role snapshot).
        e.HasOne(x => x.Guild).WithMany().HasForeignKey(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class GuildChannelConfiguration : IEntityTypeConfiguration<GuildChannel>
{
    public void Configure(EntityTypeBuilder<GuildChannel> e)
    {
        e.HasKey(x => new { x.GuildId, x.ChannelId });
        e.HasIndex(x => new { x.GuildId, x.Kind });
        // Background reconcile + the admin "tracked channels" list filter on Mode.
        e.HasIndex(x => new { x.GuildId, x.Mode });
        e.Property(x => x.Name).HasMaxLength(100);
    }
}
