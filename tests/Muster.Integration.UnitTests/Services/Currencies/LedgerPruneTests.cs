using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Persistence;
using Xunit;

namespace Muster.IntegrationTests;

public class LedgerPruneTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static LedgerPruneService MakePrune(MusterDbContext db, int globalMaxDays = 0) =>
        new(db, Options.Create(new CurrencyRetentionOptions { MaxLedgerRetentionDays = globalMaxDays }),
            NullLogger<LedgerPruneService>.Instance);

    [Theory]
    [InlineData(0, 0, 0)]      // neither limits → forever
    [InlineData(10, 0, 10)]    // no platform cap → guild value
    [InlineData(0, 30, 30)]    // guild unset → inherit platform default
    [InlineData(10, 30, 10)]   // guild shorter than cap → guild value
    [InlineData(90, 30, 30)]   // guild longer than cap → clamped to cap
    public void EffectiveRetention_CombinesGuildAndPlatform(int guild, int global, int expected)
        => Assert.Equal(expected, LedgerRetention.Effective(guild, global));

    [Theory]
    [InlineData(90, 30, true)]
    [InlineData(30, 30, false)]
    [InlineData(10, 0, false)] // no cap → never exceeds
    public void ExceedsCap_FlagsOverlongGuildValues(int guild, int global, bool exceeds)
        => Assert.Equal(exceeds, LedgerRetention.ExceedsCap(guild, global));

    private static async Task<(MusterDbContext db, Guid coinId)> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1, seedDefaults: false);
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();
        return (db, coin.Id);
    }

    private static void AddEntry(MusterDbContext db, Guid coinId, long amount, DateTimeOffset occurredAt) =>
        db.CurrencyLedgerEntries.Add(new CurrencyLedgerEntry
        {
            GuildId = 1, UserId = 10, CurrencyId = coinId, Amount = amount,
            SourceType = CurrencyLedgerSource.Connector, OccurredAt = occurredAt, Reason = "test",
        });

    [Fact]
    public async Task Prune_FoldsOldEntriesIntoCheckpoint_PreservingBalance()
    {
        var (db, coinId) = await SeededAsync();
        var now = DateTimeOffset.UtcNow;
        AddEntry(db, coinId, 100, now.AddDays(-60));
        AddEntry(db, coinId, -30, now.AddDays(-50));
        AddEntry(db, coinId, 20, now.AddDays(-5)); // within the 30-day window
        await db.SaveChangesAsync();

        var currency = new CurrencyService(db, new RecordingMessageBus());
        var before = await currency.GetBalanceAsync(1, "COIN", 10);
        Assert.Equal(90, before);

        var pruned = await MakePrune(db).PruneAsync(1, retentionDays: 30);

        Assert.Equal(2, pruned); // the two pre-cutoff rows folded
        // Balance (ledger SUM — the authority) is unchanged.
        Assert.Equal(90, await currency.GetBalanceAsync(1, "COIN", 10));
        // History compacted: one checkpoint (+70) + the recent row (+20).
        var entries = await db.CurrencyLedgerEntries.Where(e => e.UserId == 10).ToListAsync();
        Assert.Equal(2, entries.Count);
        var checkpoint = Assert.Single(entries, e => e.SourceType == CurrencyLedgerSource.Checkpoint);
        Assert.Equal(70, checkpoint.Amount);
    }

    [Fact]
    public async Task Prune_RebuildWalletsStillCorrectAfterCompaction()
    {
        var (db, coinId) = await SeededAsync();
        var now = DateTimeOffset.UtcNow;
        AddEntry(db, coinId, 100, now.AddDays(-60));
        AddEntry(db, coinId, 25, now.AddDays(-2));
        await db.SaveChangesAsync();

        var currency = new CurrencyService(db, new RecordingMessageBus());
        await MakePrune(db).PruneAsync(1, retentionDays: 30);
        await currency.RebuildWalletsAsync(1);

        var wallet = await db.Wallets.SingleAsync(w => w.UserId == 10 && w.CurrencyId == coinId);
        Assert.Equal(125, wallet.Balance);
    }

    [Fact]
    public async Task PruneAll_AppliesPlatformCapAndDefault()
    {
        var (db, coinId) = await SeededAsync(); // guild keeps LedgerRetentionDays = 0 (inherit)
        var now = DateTimeOffset.UtcNow;
        AddEntry(db, coinId, 100, now.AddDays(-60));
        AddEntry(db, coinId, 10, now.AddDays(-2));
        await db.SaveChangesAsync();

        // Global default/cap = 30d; the unset guild inherits it, so the 60-day-old row is folded.
        var pruned = await MakePrune(db, globalMaxDays: 30).PruneAllAsync();

        Assert.Equal(1, pruned);
        Assert.Equal(110, await new CurrencyService(db, new RecordingMessageBus()).GetBalanceAsync(1, "COIN", 10));
    }

    [Fact]
    public async Task Prune_NoOpWhenRetentionZeroOrNothingStale()
    {
        var (db, coinId) = await SeededAsync();
        AddEntry(db, coinId, 50, DateTimeOffset.UtcNow.AddDays(-5));
        await db.SaveChangesAsync();
        var prune = MakePrune(db);

        Assert.Equal(0, await prune.PruneAsync(1, retentionDays: 0));   // disabled
        Assert.Equal(0, await prune.PruneAsync(1, retentionDays: 30));  // nothing older than cutoff
        Assert.Equal(1, await db.CurrencyLedgerEntries.CountAsync(e => e.UserId == 10));
    }
}
