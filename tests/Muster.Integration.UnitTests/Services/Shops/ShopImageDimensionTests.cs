using Muster.Infrastructure.Services.Shops;
using Xunit;

namespace Muster.IntegrationTests;

/// <summary>
/// The dependency-free header reader that backs upload dimension enforcement, plus the limit-description helper
/// shown under upload controls. Headers are hand-built so the parser is exercised without an image library.
/// </summary>
public class ShopImageDimensionTests
{
    [Fact]
    public void Png_ReadsDimensions()
    {
        byte[] d =
        [
            0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, // signature
            0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R', // IHDR chunk header
            0x00, 0x00, 0x01, 0x2C, // width = 300
            0x00, 0x00, 0x00, 0xC8, // height = 200
        ];
        Assert.True(ImageDimensions.TryRead(d, out var w, out var h));
        Assert.Equal(300, w);
        Assert.Equal(200, h);
    }

    [Fact]
    public void Gif_ReadsDimensions()
    {
        byte[] d =
        [
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            0x64, 0x00, // width = 100 (LE)
            0x32, 0x00, // height = 50 (LE)
        ];
        Assert.True(ImageDimensions.TryRead(d, out var w, out var h));
        Assert.Equal(100, w);
        Assert.Equal(50, h);
    }

    [Fact]
    public void Jpeg_ReadsDimensionsFromSof()
    {
        byte[] d =
        [
            0xFF, 0xD8,             // SOI
            0xFF, 0xC0,             // SOF0
            0x00, 0x11,             // segment length
            0x08,                   // precision
            0x01, 0xE0,             // height = 480
            0x02, 0x80,             // width = 640
            0x03,                   // (component count, ignored)
        ];
        Assert.True(ImageDimensions.TryRead(d, out var w, out var h));
        Assert.Equal(640, w);
        Assert.Equal(480, h);
    }

    [Fact]
    public void WebpExtended_ReadsCanvas()
    {
        byte[] d =
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x00, 0x00, 0x00, 0x00,
            (byte)'W', (byte)'E', (byte)'B', (byte)'P',
            (byte)'V', (byte)'P', (byte)'8', (byte)'X',
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // flags + reserved
            0xE7, 0x03, 0x00, // width - 1 = 999 → 1000
            0x1F, 0x03, 0x00, // height - 1 = 799 → 800
        ];
        Assert.True(ImageDimensions.TryRead(d, out var w, out var h));
        Assert.Equal(1000, w);
        Assert.Equal(800, h);
    }

    [Fact]
    public void Unknown_ReturnsFalse()
    {
        byte[] d = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        Assert.False(ImageDimensions.TryRead(d, out _, out _));
    }

    [Fact]
    public void Options_DefaultBounds_AreStandardisedPerKind()
    {
        var o = new ShopImageOptions();
        Assert.Equal(512, o.Bounds(ShopImageKind.Icon).MaxWidth);
        Assert.Equal(512, o.Bounds(ShopImageKind.Icon).MaxHeight);
        Assert.Equal(2000, o.Bounds(ShopImageKind.Listing).MaxWidth);
        Assert.Equal(1920, o.Bounds(ShopImageKind.Banner).MaxWidth);
        Assert.Equal(640, o.Bounds(ShopImageKind.Banner).MaxHeight);
    }

    [Fact]
    public void Options_Describe_IncludesTypesDimensionsAndSize()
    {
        var text = new ShopImageOptions().Describe(ShopImageKind.Icon);
        Assert.Contains("PNG", text);
        Assert.Contains("512×512", text);
        Assert.Contains("MB", text);
    }
}
