using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities;

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
            s.OwnsOne(x => x.Musters); // nested within the same settings JSON document
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
