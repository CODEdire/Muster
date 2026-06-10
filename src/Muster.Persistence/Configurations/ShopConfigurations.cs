using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities.Guilds;
using Muster.Domain.Entities.Shops;

namespace Muster.Persistence.Configurations;

public class ShopStoreConfiguration : IEntityTypeConfiguration<ShopStore>
{
    public void Configure(EntityTypeBuilder<ShopStore> e)
    {
        e.HasKey(x => x.Id);
        // Slug is the storefront handle — unique within a guild.
        e.HasIndex(x => new { x.GuildId, x.Slug }).IsUnique();
        e.HasIndex(x => new { x.GuildId, x.OwnerId });
        e.Property(x => x.Name).HasMaxLength(80);
        e.Property(x => x.Slug).HasMaxLength(80);
        e.Property(x => x.Description).HasMaxLength(1000);
        e.Property(x => x.AccentColor).HasMaxLength(16);
        e.HasIndex(x => x.StoreTypeId);
        // RowVersion configured as a concurrency token in MusterDbContext (relational providers only).
    }
}

public class ShopStoreTypeConfiguration : IEntityTypeConfiguration<ShopStoreType>
{
    public void Configure(EntityTypeBuilder<ShopStoreType> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Name }).IsUnique();
        e.Property(x => x.Name).HasMaxLength(40);
        e.Property(x => x.Icon).HasMaxLength(20);
    }
}

public class ShopCategoryConfiguration : IEntityTypeConfiguration<ShopCategory>
{
    public void Configure(EntityTypeBuilder<ShopCategory> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Name }).IsUnique();
        e.Property(x => x.Name).HasMaxLength(60);
        e.Property(x => x.Icon).HasMaxLength(20);
    }
}

public class ShopListingConfiguration : IEntityTypeConfiguration<ShopListing>
{
    public void Configure(EntityTypeBuilder<ShopListing> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Status }); // market board
        e.HasIndex(x => x.StoreId);                   // storefront
        e.HasIndex(x => new { x.GuildId, x.SellerId });
        e.HasIndex(x => x.CategoryId);
        e.Property(x => x.Name).HasMaxLength(120);
        e.Property(x => x.Description).HasMaxLength(2000);
        e.HasMany(x => x.Tags).WithOne(x => x.Listing!).HasForeignKey(x => x.ListingId);
        // RowVersion configured in MusterDbContext (relational providers only) — guards the last-unit race.
    }
}

public class ShopListingTagConfiguration : IEntityTypeConfiguration<ShopListingTag>
{
    public void Configure(EntityTypeBuilder<ShopListingTag> e)
    {
        e.HasKey(x => new { x.ListingId, x.Tag });
        e.HasIndex(x => x.Tag); // tag-based market search
        e.Property(x => x.Tag).HasMaxLength(40);
    }
}

public class ShopOrderConfiguration : IEntityTypeConfiguration<ShopOrder>
{
    public void Configure(EntityTypeBuilder<ShopOrder> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Status });
        e.HasIndex(x => x.ListingId);
        e.HasIndex(x => new { x.GuildId, x.BuyerId });
        e.HasIndex(x => new { x.GuildId, x.SellerId });
        e.Property(x => x.ItemNameSnapshot).HasMaxLength(120);
        e.Property(x => x.DisputeReason).HasMaxLength(1000);
        e.HasOne(x => x.Listing).WithMany().HasForeignKey(x => x.ListingId).OnDelete(DeleteBehavior.Cascade);
        // RowVersion configured in MusterDbContext (relational providers only).
    }
}

public class GuildShopSettingsConfiguration : IEntityTypeConfiguration<GuildShopSettings>
{
    public void Configure(EntityTypeBuilder<GuildShopSettings> e)
    {
        // 1:1 with Guild, keyed + FK on GuildId (delete the guild → delete its shop settings).
        e.HasKey(x => x.GuildId);
        e.Property(x => x.GuildId).ValueGeneratedNever();
        e.HasOne<Guild>().WithOne().HasForeignKey<GuildShopSettings>(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
        // AllowedCurrencyIds is a primitive collection — EF maps it to a JSON column (a small, whole-read list).
    }
}
