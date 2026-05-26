using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Muster.Domain.Entities;

namespace Muster.Persistence.Configurations;

public class SeasonConfiguration : IEntityTypeConfiguration<Season>
{
    public void Configure(EntityTypeBuilder<Season> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Status });
    }
}

public class CurrencyConfiguration : IEntityTypeConfiguration<Currency>
{
    public void Configure(EntityTypeBuilder<Currency> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Code }).IsUnique();
    }
}

public class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.UserId, x.CurrencyId, x.SeasonId });
        // Idempotency: a given source produces at most one ledger entry.
        e.HasIndex(x => new { x.SourceType, x.SourceId })
            .IsUnique()
            .HasFilter("[SourceId] IS NOT NULL");
    }
}

public class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.UserId, x.CurrencyId, x.SeasonId }).IsUnique();
    }
}
