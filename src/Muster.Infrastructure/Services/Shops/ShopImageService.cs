using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>Which kind of shop image is being uploaded — selects the dimension cap that applies.</summary>
public enum ShopImageKind { Icon, Listing, Banner }

/// <summary>Max pixel dimensions for one image kind (0 on an axis = unbounded there).</summary>
public sealed class ImageBounds
{
    public ImageBounds() { }
    public ImageBounds(int maxWidth, int maxHeight) { MaxWidth = maxWidth; MaxHeight = maxHeight; }

    public int MaxWidth { get; set; }
    public int MaxHeight { get; set; }
}

/// <summary>App-level (global) image upload limits, bound from configuration under <c>Shop</c>.</summary>
public sealed class ShopImageOptions
{
    /// <summary>Max accepted upload size in bytes (default 2 MiB).</summary>
    public long MaxImageBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Accepted content types (lower-case).</summary>
    public string[] AllowedContentTypes { get; set; } = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    /// <summary>Square store logo/icon shown on cards and lists.</summary>
    public ImageBounds Icon { get; set; } = new(512, 512);

    /// <summary>Listing product image.</summary>
    public ImageBounds Listing { get; set; } = new(2000, 2000);

    /// <summary>Wide storefront banner — ~3:1 suits both the web hero (cover) and Discord's full-image card.</summary>
    public ImageBounds Banner { get; set; } = new(1920, 640);

    /// <summary>The dimension cap that applies to a given image kind.</summary>
    public ImageBounds Bounds(ShopImageKind kind) => kind switch
    {
        ShopImageKind.Icon => Icon,
        ShopImageKind.Banner => Banner,
        _ => Listing,
    };

    /// <summary>Human-readable upload limits for a kind, e.g.
    /// "PNG, JPEG, WebP or GIF · up to 512×512 px · max 2 MB" — shown under the upload control.</summary>
    public string Describe(ShopImageKind kind)
    {
        var names = AllowedContentTypes
            .Select(t => t.Contains('/') ? t[(t.IndexOf('/') + 1)..] : t)
            .Select(t => string.Equals(t, "jpeg", StringComparison.OrdinalIgnoreCase) ? "JPEG" : t.ToUpperInvariant())
            .ToArray();
        var types = names.Length switch
        {
            0 => "Images",
            1 => names[0],
            _ => $"{string.Join(", ", names[..^1])} or {names[^1]}",
        };

        var b = Bounds(kind);
        var dim = b.MaxWidth > 0 && b.MaxHeight > 0 ? $" · up to {b.MaxWidth}×{b.MaxHeight} px" : "";
        var size = $" · max {MaxImageBytes / (1024d * 1024d):0.#} MB";
        return $"{types}{dim}{size}";
    }
}

/// <summary>Outcome of an image upload attempt.</summary>
public enum ShopImageUploadResult { Ok, TooLarge, UnsupportedType, Empty, TooLargeDimensions }

/// <summary>
/// Stores shop listing/storefront images in the <c>shopimages</c> blob container. The DB only ever holds the blob
/// <i>key</i> (never bytes); display goes through an app endpoint that streams the blob back (no public container /
/// SAS needed). Uploads are validated for size, content type, and (per <see cref="ShopImageKind"/>) pixel
/// dimensions. Phase 1 uses the original as its own thumbnail — real server-side downscaling is a later add.
/// </summary>
public interface IShopImageService
{
    Task<(ShopImageUploadResult Result, string? Key)> UploadAsync(
        Stream content, long length, string? contentType, ShopImageKind kind, CancellationToken ct = default);

    /// <summary>Open a stored image for streaming back to a client (null if it doesn't exist).</summary>
    Task<(Stream Content, string ContentType)?> OpenAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string? key, CancellationToken ct = default);
}

/// <summary>No-op fallback used in hosts where blob storage isn't wired (bot, migration, local-without-Aspire).
/// Uploads report empty, opens return null, deletes do nothing — so the shared <c>ShopService</c> can always
/// call image cleanup without a null check. Web replaces this with the real <see cref="ShopImageService"/>.</summary>
public sealed class NoOpShopImageService : IShopImageService
{
    public Task<(ShopImageUploadResult Result, string? Key)> UploadAsync(Stream content, long length, string? contentType, ShopImageKind kind, CancellationToken ct = default)
        => Task.FromResult<(ShopImageUploadResult, string?)>((ShopImageUploadResult.Empty, null));

    public Task<(Stream Content, string ContentType)?> OpenAsync(string key, CancellationToken ct = default)
        => Task.FromResult<(Stream, string)?>(null);

    public Task DeleteAsync(string? key, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class ShopImageService(BlobContainerClient container, IOptions<ShopImageOptions> options) : IShopImageService
{
    private ShopImageOptions Opt => options.Value;

    public async Task<(ShopImageUploadResult Result, string? Key)> UploadAsync(
        Stream content, long length, string? contentType, ShopImageKind kind, CancellationToken ct = default)
    {
        if (length <= 0)
        {
            return (ShopImageUploadResult.Empty, null);
        }

        if (length > Opt.MaxImageBytes)
        {
            return (ShopImageUploadResult.TooLarge, null);
        }

        var type = contentType?.Trim().ToLowerInvariant();
        if (type is null || !Opt.AllowedContentTypes.Contains(type))
        {
            return (ShopImageUploadResult.UnsupportedType, null);
        }

        // Buffer once so we can both measure pixel dimensions and upload from the start.
        await using var buffer = await BufferAsync(content, ct);

        var bounds = Opt.Bounds(kind);
        if ((bounds.MaxWidth > 0 || bounds.MaxHeight > 0)
            && ImageDimensions.TryRead(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), out var w, out var h)
            && ((bounds.MaxWidth > 0 && w > bounds.MaxWidth) || (bounds.MaxHeight > 0 && h > bounds.MaxHeight)))
        {
            return (ShopImageUploadResult.TooLargeDimensions, null);
        }

        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        buffer.Position = 0;
        var key = $"{Guid.NewGuid():N}{Extension(type)}";
        var blob = container.GetBlobClient(key);
        await blob.UploadAsync(buffer, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = type } }, ct);
        return (ShopImageUploadResult.Ok, key);
    }

    private static async Task<MemoryStream> BufferAsync(Stream content, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    public async Task<(Stream Content, string ContentType)?> OpenAsync(string key, CancellationToken ct = default)
    {
        var blob = container.GetBlobClient(key);
        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }

        var resp = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return (resp.Value.Content, resp.Value.Details.ContentType ?? "application/octet-stream");
    }

    public async Task DeleteAsync(string? key, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(key))
        {
            await container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);
        }
    }

    private static string Extension(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".bin",
    };
}

/// <summary>
/// Reads pixel dimensions from the header bytes of PNG / GIF / JPEG / WebP images — enough to enforce upload
/// dimension caps without pulling in an image-decoding dependency. Returns false (caller should allow the upload)
/// when the format isn't recognised or the header is truncated, so a slightly-odd-but-valid file is never wrongly
/// rejected on dimensions alone (content type is already whitelisted upstream).
/// </summary>
public static class ImageDimensions
{
    public static bool TryRead(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = height = 0;
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
        {
            return TryReadPng(d, out width, out height);
        }

        if (d.Length >= 10 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F')
        {
            width = d[6] | (d[7] << 8);
            height = d[8] | (d[9] << 8);
            return true;
        }

        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            return TryReadJpeg(d, out width, out height);
        }

        if (d.Length >= 30 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F'
            && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P')
        {
            return TryReadWebp(d, out width, out height);
        }

        return false;
    }

    private static bool TryReadPng(ReadOnlySpan<byte> d, out int width, out int height)
    {
        // 8-byte signature, then IHDR chunk: length(4) + "IHDR"(4) + width(4 BE) + height(4 BE).
        width = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
        height = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
        return width > 0 && height > 0;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = height = 0;
        var pos = 2;
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            var marker = d[pos + 1];
            pos += 2;

            // Markers without a length payload.
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (pos + 2 > d.Length)
            {
                break;
            }

            var segLen = (d[pos] << 8) | d[pos + 1];

            // Start-of-frame markers carry the dimensions (skip DHT/JPG/DAC).
            if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (pos + 7 <= d.Length)
                {
                    height = (d[pos + 3] << 8) | d[pos + 4];
                    width = (d[pos + 5] << 8) | d[pos + 6];
                    return width > 0 && height > 0;
                }

                break;
            }

            if (segLen < 2)
            {
                break;
            }

            pos += segLen;
        }

        return false;
    }

    private static bool TryReadWebp(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = height = 0;

        // Format fourCC at offset 12.
        if (d[12] == (byte)'V' && d[13] == (byte)'P' && d[14] == (byte)'8' && d[15] == (byte)' ')
        {
            // Lossy: frame tag (3) at 20, start code 9D 01 2A at 23, then 14-bit width/height.
            if (d.Length < 30)
            {
                return false;
            }

            width = ((d[26] | (d[27] << 8)) & 0x3FFF);
            height = ((d[28] | (d[29] << 8)) & 0x3FFF);
            return width > 0 && height > 0;
        }

        if (d[12] == (byte)'V' && d[13] == (byte)'P' && d[14] == (byte)'8' && d[15] == (byte)'L')
        {
            // Lossless: 0x2F signature at 20, then 14-bit width-1 / height-1 packed across 4 bytes.
            if (d.Length < 25 || d[20] != 0x2F)
            {
                return false;
            }

            int b0 = d[21], b1 = d[22], b2 = d[23], b3 = d[24];
            width = 1 + (((b1 & 0x3F) << 8) | b0);
            height = 1 + (((b3 & 0x0F) << 10) | (b2 << 2) | ((b1 & 0xC0) >> 6));
            return true;
        }

        if (d[12] == (byte)'V' && d[13] == (byte)'P' && d[14] == (byte)'8' && d[15] == (byte)'X')
        {
            // Extended: 24-bit canvas width-1 at 24, height-1 at 27 (little-endian).
            if (d.Length < 30)
            {
                return false;
            }

            width = 1 + (d[24] | (d[25] << 8) | (d[26] << 16));
            height = 1 + (d[27] | (d[28] << 8) | (d[29] << 16));
            return true;
        }

        return false;
    }
}
