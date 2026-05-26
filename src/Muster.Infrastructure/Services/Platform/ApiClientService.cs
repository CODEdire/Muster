using System.Security.Cryptography;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Entities;

namespace Muster.Infrastructure.Services.Platform;

public record ApiClientCreated(ApiClient Client, string ApiKey);

/// <summary>
/// Manages API clients for the public API. Keys are random, returned once at creation, and stored only
/// as a SHA-256 hash. Validation hashes the presented key and matches an active client.
/// </summary>
public class ApiClientService(MusterDbContext db)
{
    private const string KeyPrefix = "msk_";

    /// <summary>Create an API client. A key may be bound to act as an actor (<paramref name="actsAsUserId"/>) — but
    /// only the <paramref name="createdByUserId"/>'s own account or a bot/app member of the guild, never an
    /// arbitrary member (prevents impersonating a non-consenting user). Throws on an invalid binding.</summary>
    public async Task<ApiClientCreated> CreateAsync(
        ulong guildId, string name, IEnumerable<string> scopes, ulong actsAsUserId = 0, ulong createdByUserId = 0, CancellationToken ct = default)
    {
        if (actsAsUserId != 0 && actsAsUserId != createdByUserId && !await db.IsGuildBotAsync(guildId, actsAsUserId, ct))
        {
            throw new InvalidOperationException(
                "A key can only act as your own account or a bot/app member of this server.");
        }

        var rawKey = KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

        var client = new ApiClient
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Name = name,
            ApiKeyHash = Hash(rawKey),
            Scopes = scopes.ToList(),
            ActsAsUserId = actsAsUserId,
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
        return await db.FindActiveApiClientByHashAsync(hash, ct);
    }

    public async Task<IReadOnlyList<ApiClient>> ListAsync(ulong guildId, CancellationToken ct = default)
        => await db.ListApiClientsAsync(guildId, ct);

    public async Task RevokeAsync(ulong guildId, Guid clientId, CancellationToken ct = default)
    {
        var client = await db.FindApiClientAsync(guildId, clientId, ct);
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
