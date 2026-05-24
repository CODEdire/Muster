using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;

namespace Muster.Infrastructure.Services;

public record ApiClientCreated(ApiClient Client, string ApiKey);

/// <summary>
/// Manages API clients for the public API. Keys are random, returned once at creation, and stored only
/// as a SHA-256 hash. Validation hashes the presented key and matches an active client.
/// </summary>
public class ApiClientService(MusterDbContext db)
{
    private const string KeyPrefix = "msk_";

    public async Task<ApiClientCreated> CreateAsync(
        ulong guildId, string name, IEnumerable<string> scopes, CancellationToken ct = default)
    {
        var rawKey = KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

        var client = new ApiClient
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Name = name,
            ApiKeyHash = Hash(rawKey),
            Scopes = scopes.ToList(),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.ApiClients.Add(client);
        await db.SaveChangesAsync(ct);
        return new ApiClientCreated(client, rawKey);
    }

    public async Task<ApiClient?> ValidateAsync(string rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return null;
        }

        var hash = Hash(rawKey);
        return await db.ApiClients.FirstOrDefaultAsync(c => c.IsActive && c.ApiKeyHash == hash, ct);
    }

    public async Task<IReadOnlyList<ApiClient>> ListAsync(ulong guildId, CancellationToken ct = default)
        => await db.ApiClients.Where(c => c.GuildId == guildId).OrderBy(c => c.Name).ToListAsync(ct);

    public async Task RevokeAsync(ulong guildId, Guid clientId, CancellationToken ct = default)
    {
        var client = await db.ApiClients.FirstOrDefaultAsync(c => c.Id == clientId && c.GuildId == guildId, ct);
        if (client is not null)
        {
            client.IsActive = false;
            await db.SaveChangesAsync(ct);
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
