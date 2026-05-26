using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;

namespace Muster.IntegrationTests;

public class CurrencyAdminTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        return db;
    }

    [Fact]
    public async Task Create_SpendableCoin_Succeeds_AndListsAlongsidePoints()
    {
        using var db = await SeededAsync();
        var sut = new CurrencyAdminService(db);

        var result = await sut.CreateAsync(1, "coin", "Coin", isSeasonal: false, isSpendable: true, CurrencyMode.Internal);
        Assert.False(result.IsError);

        var list = await sut.ListAsync(1);
        Assert.Contains(list, c => c.Code == "COIN" && c.IsSpendable && !c.IsSeasonal);
        Assert.Contains(list, c => c is { Code: "POINTS", IsSystem: true });
    }

    [Fact]
    public async Task Create_Rejects_BadCode_Reserved_AndDuplicate()
    {
        using var db = await SeededAsync();
        var sut = new CurrencyAdminService(db);

        Assert.True((await sut.CreateAsync(1, "x", "X", false, true, CurrencyMode.Internal)).IsError);       // too short
        Assert.True((await sut.CreateAsync(1, "POINTS", "P", true, false, CurrencyMode.Internal)).IsError);  // reserved
        Assert.False((await sut.CreateAsync(1, "COIN", "Coin", false, true, CurrencyMode.Internal)).IsError);
        Assert.True((await sut.CreateAsync(1, "coin", "Dup", false, true, CurrencyMode.Internal)).IsError);  // duplicate
    }

    [Fact]
    public async Task Update_ChangesNameSpendableMode()
    {
        using var db = await SeededAsync();
        var sut = new CurrencyAdminService(db);
        await sut.CreateAsync(1, "COIN", "Coin", false, true, CurrencyMode.Internal);
        var coin = (await sut.ListAsync(1)).Single(c => c.Code == "COIN");

        var result = await sut.UpdateAsync(1, coin.Id, "Aurum", isSpendable: true, CurrencyMode.External);
        Assert.False(result.IsError);

        var updated = (await sut.ListAsync(1)).Single(c => c.Code == "COIN");
        Assert.Equal("Aurum", updated.Name);
        Assert.Equal(CurrencyMode.External, updated.Mode);
    }

    [Fact]
    public async Task ListAsync_ToleratesConnectorWithNullSubObjects()
    {
        using var db = await SeededAsync();
        // Simulate a row stored before the v2 connector graph existed: nested owned objects materialize as null.
        var coin = new Currency { Id = Guid.NewGuid(), GuildId = 1, Code = "COIN", Name = "Coin", IsSpendable = true, Mode = CurrencyMode.External };
        coin.Connector.Auth = null!;
        coin.Connector.Signing = null!;
        coin.Connector.Credit = null!;
        coin.Connector.Debit = null!;
        coin.Connector.GetBalance = null!;
        db.Currencies.Add(coin);
        await db.SaveChangesAsync();

        var view = (await new CurrencyAdminService(db).ListAsync(1)).Single(c => c.Code == "COIN").Connector;

        Assert.NotNull(view.Config.Auth); // normalized — no NRE
        Assert.False(view.AuthHasSecret);
    }

    [Fact]
    public async Task SetConnector_PersistsConfig_HidesSecret_AndKeepsSecretWhenBlank()
    {
        using var db = await SeededAsync();
        var sut = new CurrencyAdminService(db); // no protector → secrets stored as-is (test asserts round-trip)
        await sut.CreateAsync(1, "COIN", "Coin", false, true, CurrencyMode.External);
        var coin = (await sut.ListAsync(1)).Single(c => c.Code == "COIN");

        var set = await sut.SetConnectorAsync(1, coin.Id, new CurrencyConnector
        {
            Enabled = true,
            BaseUrl = "https://loot.example",
            Auth = new ConnectorAuth { Scheme = ConnectorAuthScheme.ApiKey, ApiKeyName = "x-api-key", Secret = "s3cr3t" },
            Credit = new ConnectorAction { Enabled = true, Method = "POST", Path = "/currency/add-cruor", BodyTemplate = "{\"cruor_amount\": {amount}}" },
        });
        Assert.False(set.IsError);

        var view = (await sut.ListAsync(1)).Single(c => c.Code == "COIN").Connector;
        Assert.True(view.Config.Enabled);
        Assert.Equal("https://loot.example", view.Config.BaseUrl);
        Assert.Equal(ConnectorAuthScheme.ApiKey, view.Config.Auth.Scheme);
        Assert.True(view.AuthHasSecret);            // set…
        Assert.Null(view.Config.Auth.Secret);       // …but never returned

        // Re-save with a blank secret → the stored one is kept.
        await sut.SetConnectorAsync(1, coin.Id, new CurrencyConnector
        {
            Enabled = false,
            BaseUrl = "https://loot.example",
            Auth = new ConnectorAuth { Scheme = ConnectorAuthScheme.ApiKey, ApiKeyName = "x-api-key", Secret = "" },
        });
        var kept = await db.Currencies.SingleAsync(c => c.Code == "COIN");
        Assert.Equal("s3cr3t", kept.Connector.Auth.Secret);
        Assert.False(kept.Connector.Enabled);
    }
}
