using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;

namespace Muster.IntegrationTests;

public class ApiAndCurrencyTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync(ulong guildId = 1)
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(guildId, "G", null, ownerId: 1);
        return db;
    }

    private static async Task<Currency> AddCoinAsync(MusterDbContext db, ulong guildId = 1, CurrencyMode mode = CurrencyMode.Internal)
    {
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = guildId, Code = "COIN", Name = "Coin", IsSpendable = true, Mode = mode };
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();
        return coin;
    }

    [Fact]
    public async Task ApiClient_Create_Validate_Revoke()
    {
        using var db = await SeededAsync();
        var sut = new ApiClientService(db);

        var created = await sut.CreateAsync(1, "loot-bot", ["read:ledger", "write:currency"]);
        Assert.StartsWith("msk_", created.ApiKey);

        var validated = await sut.ValidateAsync(created.ApiKey);
        Assert.NotNull(validated);
        Assert.Equal(1ul, validated!.GuildId);
        Assert.Contains("write:currency", validated.Scopes);

        Assert.Null(await sut.ValidateAsync("msk_wrong"));

        await sut.RevokeAsync(1, created.Client.Id);
        Assert.Null(await sut.ValidateAsync(created.ApiKey)); // revoked → inactive
    }

    [Fact]
    public async Task ApiClient_KeyIsNotStoredInPlaintext()
    {
        using var db = await SeededAsync();
        var created = await new ApiClientService(db).CreateAsync(1, "c", ["read:ledger"]);

        var stored = await db.ApiClients.SingleAsync();
        Assert.NotEqual(created.ApiKey, stored.ApiKeyHash);
        Assert.DoesNotContain(created.ApiKey, stored.ApiKeyHash);
    }

    [Fact]
    public async Task Currency_MintThenSpend_TracksBalance_AndBlocksOverdraft()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var sut = new CurrencyService(db, new RecordingMessageBus());

        Assert.Equal(CurrencyOperationStatus.Ok, (await sut.MintAsync(1, "COIN", 10, 100, "drop")).Status);
        Assert.Equal(100, await sut.GetBalanceAsync(1, "COIN", 10));

        var spend = await sut.SpendAsync(1, "COIN", 10, 30, "buy");
        Assert.Equal(CurrencyOperationStatus.Ok, spend.Status);
        Assert.Equal(70, spend.Balance);

        var overdraft = await sut.SpendAsync(1, "COIN", 10, 1000, "buy");
        Assert.Equal(CurrencyOperationStatus.InsufficientFunds, overdraft.Status);
        Assert.Equal(70, await sut.GetBalanceAsync(1, "COIN", 10)); // unchanged
    }

    [Fact]
    public async Task Currency_MintWithExternalId_IsIdempotent()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var sut = new CurrencyService(db, new RecordingMessageBus());

        await sut.MintAsync(1, "COIN", 10, 100, "drop", sourceId: "connector:abc");
        await sut.MintAsync(1, "COIN", 10, 100, "drop", sourceId: "connector:abc"); // retried delivery
        Assert.Equal(100, await sut.GetBalanceAsync(1, "COIN", 10));                 // credited exactly once
    }

    [Fact]
    public async Task Currency_SpendWithExternalId_IsIdempotent()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var sut = new CurrencyService(db, new RecordingMessageBus());
        await sut.MintAsync(1, "COIN", 10, 100, "drop");

        var first = await sut.SpendAsync(1, "COIN", 10, 30, "buy", sourceId: "connector:s1");
        var retry = await sut.SpendAsync(1, "COIN", 10, 30, "buy", sourceId: "connector:s1"); // retried delivery
        Assert.Equal(CurrencyOperationStatus.Ok, first.Status);
        Assert.Equal(CurrencyOperationStatus.Ok, retry.Status);
        Assert.Equal(70, await sut.GetBalanceAsync(1, "COIN", 10));                            // debited exactly once
    }

    [Fact]
    public async Task RebuildWallets_CorrectsDriftFromTheLedger()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var sut = new CurrencyService(db, new RecordingMessageBus());
        await sut.MintAsync(1, "COIN", 10, 100, "drop");

        // Corrupt the cached wallet so it disagrees with the ledger.
        var wallet = await db.Wallets.SingleAsync(w => w.UserId == 10);
        wallet.Balance = 5;
        await db.SaveChangesAsync();

        var changed = await sut.RebuildWalletsAsync(1);

        Assert.Equal(1, changed);
        Assert.Equal(100, await sut.GetBalanceAsync(1, "COIN", 10)); // recomputed from the ledger
    }

    [Fact]
    public async Task Currency_ExternalMode_SkipsOverdraftCheck()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db, mode: CurrencyMode.External);
        var sut = new CurrencyService(db, new RecordingMessageBus());

        // No balance, but External mode means the external system owns the balance — we just shadow it.
        var spend = await sut.SpendAsync(1, "COIN", 10, 50, "external spend");
        Assert.Equal(CurrencyOperationStatus.Ok, spend.Status);
    }

    [Fact]
    public async Task Currency_UnknownCode_ReturnsNotFound()
    {
        using var db = await SeededAsync();
        var sut = new CurrencyService(db, new RecordingMessageBus());

        Assert.Equal(CurrencyOperationStatus.CurrencyNotFound, (await sut.MintAsync(1, "NOPE", 10, 5, "x")).Status);
        Assert.Null(await sut.GetBalanceAsync(1, "NOPE", 10));
    }

    [Fact]
    public async Task Currency_Transfer_MovesFunds_AndChecksOverdraft()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var sut = new CurrencyService(db, new RecordingMessageBus());
        await sut.MintAsync(1, "COIN", 10, 100, "drop");

        var ok = await sut.TransferAsync(1, "COIN", fromUserId: 10, toUserId: 20, amount: 30, "gift");
        Assert.Equal(CurrencyOperationStatus.Ok, ok.Status);
        Assert.Equal(70, ok.Balance);                          // sender's resulting balance
        Assert.Equal(70, await sut.GetBalanceAsync(1, "COIN", 10));
        Assert.Equal(30, await sut.GetBalanceAsync(1, "COIN", 20));

        var overdraft = await sut.TransferAsync(1, "COIN", 10, 20, 1000, "gift");
        Assert.Equal(CurrencyOperationStatus.InsufficientFunds, overdraft.Status);
        Assert.Equal(70, await sut.GetBalanceAsync(1, "COIN", 10)); // unchanged
        Assert.Equal(30, await sut.GetBalanceAsync(1, "COIN", 20)); // unchanged
    }

    [Fact]
    public async Task Currency_Transfer_Idempotent_OnSourceId()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var sut = new CurrencyService(db, new RecordingMessageBus());
        await sut.MintAsync(1, "COIN", 10, 100, "drop");

        await sut.TransferAsync(1, "COIN", 10, 20, 30, "gift", sourceId: "xfer:1");
        await sut.TransferAsync(1, "COIN", 10, 20, 30, "gift", sourceId: "xfer:1"); // retried
        Assert.Equal(70, await sut.GetBalanceAsync(1, "COIN", 10));                 // moved exactly once
        Assert.Equal(30, await sut.GetBalanceAsync(1, "COIN", 20));
    }

    [Fact]
    public async Task Currency_Adjust_AppliesSignedDelta()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var sut = new CurrencyService(db, new RecordingMessageBus());

        Assert.Equal(CurrencyOperationStatus.Ok, (await sut.AdjustAsync(1, "COIN", 10, 100, "mint")).Status);
        Assert.Equal(100, await sut.GetBalanceAsync(1, "COIN", 10));

        var down = await sut.AdjustAsync(1, "COIN", 10, -40, "correction");
        Assert.Equal(CurrencyOperationStatus.Ok, down.Status);
        Assert.Equal(60, down.Balance);

        // Adjustment-source entries land under CurrencyLedgerSource.Adjustment (staff correction trail).
        Assert.Equal(2, await db.CurrencyLedgerEntries.CountAsync(e => e.SourceType == CurrencyLedgerSource.Adjustment));
    }

    [Fact]
    public async Task MemberHistory_NewestFirst_AndCurrencyFilter()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var sut = new CurrencyService(db, new RecordingMessageBus());
        var read = new CurrencyReadService(db);

        await sut.MintAsync(1, "COIN", 10, 100, "drop");
        await sut.AwardPointsAsync(1, 10, 25, CurrencyLedgerSource.Quest, "q1", "win");
        await sut.MintAsync(1, "COIN", 10, 30, "drop2");

        var all = await read.GetMemberHistoryAsync(1, userId: 10, code: null, skip: 0, take: 50);
        Assert.Equal(3, all.Count);
        Assert.Equal("drop2", all[0].Reason); // newest first

        var coinOnly = await read.GetMemberHistoryAsync(1, userId: 10, code: "COIN", skip: 0, take: 50);
        Assert.Equal(2, coinOnly.Count);
        Assert.All(coinOnly, e => Assert.Equal("COIN", e.CurrencyCode));

        Assert.Empty(await read.GetMemberHistoryAsync(1, userId: 10, code: "NOPE", skip: 0, take: 50));
    }

    [Fact]
    public async Task Leaderboard_AnyCurrency_OrdersByCachedBalance()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var sut = new CurrencyService(db, new RecordingMessageBus());
        await sut.MintAsync(1, "COIN", 10, 100, "a");
        await sut.MintAsync(1, "COIN", 20, 250, "b");

        var board = await new CurrencyReadService(db).GetLeaderboardAsync(1, "COIN", top: 25);

        Assert.Equal(2, board.Count);
        Assert.Equal(20ul, board[0].UserId); // 250 ahead of 100
        Assert.Equal(250, board[0].Total);
        Assert.Empty(await new CurrencyReadService(db).GetLeaderboardAsync(1, "NOPE"));
    }

    [Fact]
    public async Task GetCurrencies_ListsDirectory_CodeOrdered_WithFlags()
    {
        using var db = await SeededAsync(); // provisioning seeds seasonal POINTS
        await AddCoinAsync(db);             // COIN: spendable, non-seasonal

        var dir = await new CurrencyReadService(db).GetCurrenciesAsync(1);

        Assert.Equal(new[] { "COIN", "POINTS" }, dir.Select(c => c.Code).ToArray()); // code-ordered
        var coin = Assert.Single(dir, c => c.Code == "COIN");
        Assert.True(coin.Spendable);
        Assert.False(coin.Seasonal);
        Assert.True(Assert.Single(dir, c => c.Code == "POINTS").Seasonal);
    }

    [Fact]
    public void CurrencyAuthorizer_Allows_EnforcesOwnerVsStaff()
    {
        var sut = new CurrencyAuthorizer(null!); // pure rule needs no DB
        var member = new Muster.Domain.GuildActor(1, 10);

        // Members act on their own wallet only.
        Assert.True(sut.Allows(member, isManager: false, subjectUserId: 10, CurrencyPermission.Transfer));
        Assert.True(sut.Allows(member, isManager: false, subjectUserId: 10, CurrencyPermission.Spend));
        Assert.False(sut.Allows(member, isManager: false, subjectUserId: 20, CurrencyPermission.Transfer));

        // Mint/Adjust are staff-only, regardless of subject.
        Assert.False(sut.Allows(member, isManager: false, subjectUserId: 10, CurrencyPermission.Mint));
        Assert.True(sut.Allows(member, isManager: true, subjectUserId: 20, CurrencyPermission.Mint));
        Assert.True(sut.Allows(member, isManager: true, subjectUserId: 20, CurrencyPermission.Adjust));
    }

    [Fact]
    public async Task MintAndSpendCommandHandlers_ReturnCqrsResult()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var currency = new CurrencyService(db, new RecordingMessageBus());

        const ulong Actor = 1;

        var minted = await Muster.Infrastructure.Messaging.MintCurrencyHandler.Handle(
            new Muster.Contracts.MintCurrency(1, Actor, "COIN", 10, 100, "drop"), currency, CancellationToken.None);
        Assert.True(minted.Success);
        Assert.Equal(100, minted.Balance);

        var spent = await Muster.Infrastructure.Messaging.SpendCurrencyHandler.Handle(
            new Muster.Contracts.SpendCurrency(1, Actor, "COIN", 10, 40, "buy"), currency, CancellationToken.None);
        Assert.True(spent.Success);
        Assert.Equal(60, spent.Balance);

        var overdraft = await Muster.Infrastructure.Messaging.SpendCurrencyHandler.Handle(
            new Muster.Contracts.SpendCurrency(1, Actor, "COIN", 10, 1000, "buy"), currency, CancellationToken.None);
        Assert.False(overdraft.Success);
        Assert.Equal("InsufficientFunds", overdraft.Status);
    }
}
