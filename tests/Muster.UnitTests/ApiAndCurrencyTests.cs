using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

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
        var sut = new CurrencyService(db, new AwardService(db));

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
    public async Task Currency_ExternalMode_SkipsOverdraftCheck()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db, mode: CurrencyMode.External);
        var sut = new CurrencyService(db, new AwardService(db));

        // No balance, but External mode means the external system owns the balance — we just shadow it.
        var spend = await sut.SpendAsync(1, "COIN", 10, 50, "external spend");
        Assert.Equal(CurrencyOperationStatus.Ok, spend.Status);
    }

    [Fact]
    public async Task Currency_UnknownCode_ReturnsNotFound()
    {
        using var db = await SeededAsync();
        var sut = new CurrencyService(db, new AwardService(db));

        Assert.Equal(CurrencyOperationStatus.CurrencyNotFound, (await sut.MintAsync(1, "NOPE", 10, 5, "x")).Status);
        Assert.Null(await sut.GetBalanceAsync(1, "NOPE", 10));
    }

    [Fact]
    public async Task MintAndSpendCommandHandlers_ReturnCqrsResult()
    {
        using var db = await SeededAsync();
        await AddCoinAsync(db);
        var currency = new CurrencyService(db, new AwardService(db));

        var minted = await Muster.Infrastructure.Messaging.MintCurrencyHandler.Handle(
            new Muster.Contracts.MintCurrency(1, "COIN", 10, 100, "drop"), currency, CancellationToken.None);
        Assert.True(minted.Success);
        Assert.Equal(100, minted.Balance);

        var spent = await Muster.Infrastructure.Messaging.SpendCurrencyHandler.Handle(
            new Muster.Contracts.SpendCurrency(1, "COIN", 10, 40, "buy"), currency, CancellationToken.None);
        Assert.True(spent.Success);
        Assert.Equal(60, spent.Balance);

        var overdraft = await Muster.Infrastructure.Messaging.SpendCurrencyHandler.Handle(
            new Muster.Contracts.SpendCurrency(1, "COIN", 10, 1000, "buy"), currency, CancellationToken.None);
        Assert.False(overdraft.Success);
        Assert.Equal("InsufficientFunds", overdraft.Status);
    }
}
