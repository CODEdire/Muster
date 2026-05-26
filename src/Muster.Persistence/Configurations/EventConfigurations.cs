using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities;

namespace Muster.Persistence.Configurations;

public class GuildEventConfiguration : IEntityTypeConfiguration<GuildEvent>
{
    public void Configure(EntityTypeBuilder<GuildEvent> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Status });
        e.HasMany(x => x.Attendees).WithOne(x => x.Event!).HasForeignKey(x => x.EventId);
    }
}

public class EventAttendeeConfiguration : IEntityTypeConfiguration<EventAttendee>
{
    public void Configure(EntityTypeBuilder<EventAttendee> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.EventId, x.UserId }).IsUnique();
    }
}
