using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities.Ratings;

namespace Muster.Persistence.Configurations;

public class RatingConfiguration : IEntityTypeConfiguration<Rating>
{
    public void Configure(EntityTypeBuilder<Rating> e)
    {
        e.HasKey(x => x.Id);
        // One rating per rater per source (a buyer/seller can't rate the same order twice).
        e.HasIndex(x => new { x.Context, x.SourceId, x.RaterId }).IsUnique();
        // The reputation aggregate: a subject's revealed ratings in a role/context.
        e.HasIndex(x => new { x.GuildId, x.Context, x.SubjectId, x.Role });
        // The blind-reveal scan keys off the source.
        e.HasIndex(x => new { x.Context, x.SourceId });
        e.Property(x => x.Comment).HasMaxLength(1000);
    }
}
