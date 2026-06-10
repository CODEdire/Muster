using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Connectors;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>Reconciliation: the shadow wallet is adjusted to the external balance, idempotently, and stamped.</summary>
public class CurrencyBalanceSyncTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>().UseInMemoryDatabase($"muster-{Guid.NewGuid()}").Options);

    private static async Task<(MusterDbContext db, Currency coin, CurrencyConnectorSyncService sync)> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1, seedDefaults: false);
        var coin = new Currency
        {
            Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true,
            Mode = CurrencyMode.External, Connector = new CurrencyConnector { Enabled = true },
        };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var awards = new CurrencyService(db, new RecordingMessageBus());
        // client/secrets are unused on the knownBalance path.
        var sync = new CurrencyConnectorSyncService(db, client: null!, awards, NullLogger<CurrencyConnectorSyncService>.Instance);
        return (db, coin, sync);
    }

    [Fact]
    public async Task Reconcile_AdjustsShadowToExternal_AndStampsSyncTime()
    {
        var (db, coin, sync) = await SeededAsync();
        await new CurrencyService(db, new RecordingMessageBus()).MintAsync(1, "COIN", 10, 30, "seed"); // shadow = 30

        var result = await sync.ReconcileAsync(1, coin.Id, 10, knownBalance: 100);

        Assert.Equal(100, result);
        Assert.Equal(100, await db.BalanceAsync(1, 10, coin.Id, null)); // adjusted up by +70
        var wallet = await db.FindWalletAsync(1, 10, coin.Id, null);
        Assert.NotNull(wallet!.LastSyncedAt);
    }

    [Fact]
    public async Task Reconcile_WhenAlreadyAligned_WritesNoCurrencyLedgerEntry()
    {
        var (db, coin, sync) = await SeededAsync();
        await new CurrencyService(db, new RecordingMessageBus()).MintAsync(1, "COIN", 10, 100, "seed");
        var before = await db.CurrencyLedgerEntries.CountAsync();

        await sync.ReconcileAsync(1, coin.Id, 10, knownBalance: 100); // already 100 → delta 0

        Assert.Equal(before, await db.CurrencyLedgerEntries.CountAsync()); // no reconcile entry
        Assert.NotNull((await db.FindWalletAsync(1, 10, coin.Id, null))!.LastSyncedAt); // still stamped
    }

    [Fact]
    public async Task Reconcile_InternalCurrency_IsNoOp()
    {
        var (db, _, sync) = await SeededAsync();
        var pts = await db.FindPointsAsync(1); // seeded POINTS = Internal
        Assert.Null(await sync.ReconcileAsync(1, pts!.Id, 10, knownBalance: 999));
    }
}
