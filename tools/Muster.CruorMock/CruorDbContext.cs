using Microsoft.EntityFrameworkCore;

namespace Muster.CruorMock;

// --- persisted entities (SQLite-backed) ---------------------------------------------------------------
// IDs are long throughout: Discord snowflakes (member/user/holder) are 64-bit. Surrogate keys (item/auction
// /bid Id) are long too for uniformity. Amounts are long.
public sealed class BalanceEntity
{
    public long MemberId { get; set; }  // PK = the member id (not generated)
    public string DisplayName { get; set; } = "";
    public long Value { get; set; }
}

public sealed class ItemEntity
{
    public long Id { get; set; }        // identity
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Quantity { get; set; }
    public long HolderId { get; set; }
}

public sealed class AuctionEntity
{
    public long Id { get; set; }        // identity
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long ItemId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public long? AwardedTo { get; set; }
    public long? WinningBid { get; set; }
}

public sealed class BidEntity
{
    public long Id { get; set; }        // identity
    public long UserId { get; set; }
    public long AuctionId { get; set; }
    public long Amount { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
}

/// <summary>EF Core context backing the Cruor mock with a SQLite file (persisted via a docker volume).</summary>
public sealed class CruorDbContext(DbContextOptions<CruorDbContext> options) : DbContext(options)
{
    public DbSet<BalanceEntity> Balances => Set<BalanceEntity>();
    public DbSet<ItemEntity> Items => Set<ItemEntity>();
    public DbSet<AuctionEntity> Auctions => Set<AuctionEntity>();
    public DbSet<BidEntity> Bids => Set<BidEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BalanceEntity>().HasKey(x => x.MemberId);
        b.Entity<BalanceEntity>().Property(x => x.MemberId).ValueGeneratedNever();
        b.Entity<BidEntity>().HasIndex(x => x.AuctionId);
    }
}
