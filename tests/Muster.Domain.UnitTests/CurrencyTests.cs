using Muster.Domain.Entities;
using Xunit;

namespace Muster.UnitTests;

public class CurrencyTests
{
    [Fact]
    public void SeasonalPointsAndPersistentCoinAreDistinct()
    {
        var points = new Currency { Code = "POINTS", IsSeasonal = true, IsSpendable = false };
        var coin = new Currency { Code = "COIN", IsSeasonal = false, IsSpendable = true };

        Assert.True(points.IsSeasonal);
        Assert.False(points.IsSpendable);
        Assert.False(coin.IsSeasonal);
        Assert.True(coin.IsSpendable);
    }
}
