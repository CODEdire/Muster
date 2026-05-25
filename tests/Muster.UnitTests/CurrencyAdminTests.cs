using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;
using Xunit;

namespace Muster.UnitTests;

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
}
