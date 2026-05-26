using Muster.Infrastructure.Services.Platform;
using Xunit;

namespace Muster.UnitTests;

/// <summary>Zone-independent date parsing + the system zone list are pure, so we assert them without a DB or zone.</summary>
public class TimeZoneParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DiscordTimestamp_Parses()
    {
        Assert.True(TimeZoneService.TryParseInstant("<t:1700000000>", Now, out var u));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), u);
    }

    [Fact]
    public void DiscordTimestamp_WithStyleSuffix_Parses()
    {
        Assert.True(TimeZoneService.TryParseInstant("<t:1700000000:R>", Now, out var u));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), u);
    }

    [Fact]
    public void BareUnixSeconds_Parses()
    {
        Assert.True(TimeZoneService.TryParseInstant("1700000000", Now, out var u));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), u);
    }

    [Fact]
    public void BareUnixMillis_Parses()
    {
        Assert.True(TimeZoneService.TryParseInstant("1700000000000", Now, out var u));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), u);
    }

    [Theory]
    [InlineData("in 3 days", 3 * 24 * 60)]
    [InlineData("3 days", 3 * 24 * 60)]
    [InlineData("3d", 3 * 24 * 60)]
    [InlineData("2h", 2 * 60)]
    [InlineData("90m", 90)]
    [InlineData("1w", 7 * 24 * 60)]
    public void Relative_AddsToNow(string raw, int minutes)
    {
        Assert.True(TimeZoneService.TryParseInstant(raw, Now, out var u));
        Assert.Equal(Now.AddMinutes(minutes), u);
    }

    [Theory]
    [InlineData("2026-06-01 18:00")] // plain wall-clock → needs a zone, not an instant
    [InlineData("tomorrow")]
    [InlineData("not a date")]
    [InlineData("0d")]              // zero duration rejected
    public void PlainOrUnknown_IsNotAnInstant(string raw)
        => Assert.False(TimeZoneService.TryParseInstant(raw, Now, out _));

    [Fact]
    public void GetKnownZones_AreNonEmptyAndValidIanaIds()
    {
        var zones = TimeZoneService.GetKnownZones();
        Assert.NotEmpty(zones);
        Assert.Contains(zones, z => z.Id == "UTC");
        // Every id the picker offers must round-trip through the zone provider.
        Assert.All(zones, z => Assert.True(TimeZoneService.IsValidZone(z.Id)));
    }
}
