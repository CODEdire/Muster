using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities;

namespace Muster.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> e) => e.HasKey(x => x.Id);
}

public class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> e) => e.HasKey(x => x.Id);
}

public class PostedMessageConfiguration : IEntityTypeConfiguration<PostedMessage>
{
    public void Configure(EntityTypeBuilder<PostedMessage> e)
    {
        e.HasKey(x => x.Id);
        // One live message per entity — the consumer upserts by (type, id).
        e.HasIndex(x => new { x.EntityType, x.EntityId }).IsUnique();
        e.Property(x => x.EntityType).HasMaxLength(32);
    }
}
