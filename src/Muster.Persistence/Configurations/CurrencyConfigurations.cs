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

        // The whole connector graph lives in one JSON column ("Connector"): shared auth + signing + the three
        // action sub-objects + the static-headers collection, all nested within the same document.
        e.OwnsOne(x => x.Connector, c =>
        {
            c.ToJson();
            c.OwnsOne(x => x.Auth);
            c.OwnsOne(x => x.Signing);
            c.OwnsOne(x => x.Credit);
            c.OwnsOne(x => x.Debit);
            c.OwnsOne(x => x.GetBalance);
            c.OwnsMany(x => x.Headers);
        });
    }
}

public class CurrencyLedgerEntryConfiguration : IEntityTypeConfiguration<CurrencyLedgerEntry>
{
    public void Configure(EntityTypeBuilder<CurrencyLedgerEntry> e)
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

public class CurrencyBulkBatchConfiguration : IEntityTypeConfiguration<CurrencyBulkBatch>
{
    public void Configure(EntityTypeBuilder<CurrencyBulkBatch> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Status });
        // TargetUserIds (List<ulong>) maps to a JSON column via EF's primitive-collection support (like GuildMember.RoleIds).
    }
}

public class CurrencyWebhookConfiguration : IEntityTypeConfiguration<CurrencyWebhook>
{
    public void Configure(EntityTypeBuilder<CurrencyWebhook> e)
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.GuildId, x.Enabled });
        // SourceFilter (List<CurrencyLedgerSource>) maps to a JSON column via EF's primitive-collection support.
    }
}
