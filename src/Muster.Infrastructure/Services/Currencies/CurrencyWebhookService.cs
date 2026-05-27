using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities.Currencies;
using Muster.Domain.Enums;
using Muster.Infrastructure.Connectors;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Currencies;

/// <summary>A webhook subscription projected for the admin UI — the secret is never returned (write-only).</summary>
public record WebhookView(
    Guid Id, string Url, bool Enabled, IReadOnlyList<CurrencyLedgerSource> Sources,
    int ConsecutiveFailures, int? LastStatusCode, string? LastError, DateTimeOffset? LastDeliveryAt, bool HasSecret);

/// <summary>The outcome of a create/update/test op (mirrors the connector admin's result shape).</summary>
public record WebhookResult(bool IsError, string Message, Guid? Id = null);

/// <summary>Admin CRUD + test-send for per-guild outbound webhooks. Callers depend on this interface.</summary>
public interface ICurrencyWebhookService
{
    Task<IReadOnlyList<WebhookView>> ListAsync(ulong guildId, CancellationToken ct = default);
    Task<WebhookResult> CreateAsync(ulong guildId, string url, string? secret, IReadOnlyCollection<CurrencyLedgerSource> sources, CancellationToken ct = default);
    Task<WebhookResult> UpdateAsync(ulong guildId, Guid id, string url, string? secret, IReadOnlyCollection<CurrencyLedgerSource> sources, CancellationToken ct = default);
    Task<WebhookResult> SetEnabledAsync(ulong guildId, Guid id, bool enabled, CancellationToken ct = default);
    Task<WebhookResult> DeleteAsync(ulong guildId, Guid id, CancellationToken ct = default);
    Task<WebhookResult> TestSendAsync(ulong guildId, Guid id, CancellationToken ct = default);
}

/// <summary>
/// Admin CRUD for per-guild outbound webhooks: validates the endpoint (absolute HTTPS), encrypts the HMAC secret at
/// rest (reusing the connector's <see cref="IConnectorSecretProtector"/>), and can fire a signed test delivery.
/// Delivery itself is driven by <see cref="CurrencyWebhookDispatcher"/> from the movement fan-out handler.
/// </summary>
public sealed class CurrencyWebhookService(MusterDbContext db, IConnectorSecretProtector secrets, CurrencyWebhookDispatcher dispatcher) : ICurrencyWebhookService
{
    public async Task<IReadOnlyList<WebhookView>> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var hooks = await db.ListWebhooksAsync(guildId, ct);
        return hooks.Select(ToView).ToList();
    }

    public async Task<WebhookResult> CreateAsync(
        ulong guildId, string url, string? secret, IReadOnlyCollection<CurrencyLedgerSource> sources, CancellationToken ct = default)
    {
        if (!IsValidUrl(url))
        {
            return new WebhookResult(true, "Enter an absolute https:// URL.");
        }

        var hook = new CurrencyWebhook
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Url = url.Trim(),
            Secret = string.IsNullOrWhiteSpace(secret) ? null : secrets.Protect(secret.Trim()),
            SourceFilter = sources.Distinct().ToList(),
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CurrencyWebhooks.Add(hook);
        await db.SaveChangesAsync(ct);
        return new WebhookResult(false, "Webhook added.", hook.Id);
    }

    public async Task<WebhookResult> UpdateAsync(
        ulong guildId, Guid id, string url, string? secret, IReadOnlyCollection<CurrencyLedgerSource> sources, CancellationToken ct = default)
    {
        var hook = await Find(guildId, id, ct);
        if (hook is null)
        {
            return new WebhookResult(true, "Webhook not found.");
        }

        if (!IsValidUrl(url))
        {
            return new WebhookResult(true, "Enter an absolute https:// URL.");
        }

        hook.Url = url.Trim();
        hook.SourceFilter = sources.Distinct().ToList();
        if (!string.IsNullOrWhiteSpace(secret))
        {
            hook.Secret = secrets.Protect(secret.Trim()); // blank = keep existing
        }

        await db.SaveChangesAsync(ct);
        return new WebhookResult(false, "Webhook updated.", id);
    }

    public async Task<WebhookResult> SetEnabledAsync(ulong guildId, Guid id, bool enabled, CancellationToken ct = default)
    {
        var hook = await Find(guildId, id, ct);
        if (hook is null)
        {
            return new WebhookResult(true, "Webhook not found.");
        }

        hook.Enabled = enabled;
        if (enabled)
        {
            hook.ConsecutiveFailures = 0; // re-enabling clears the failure streak that may have auto-disabled it
            hook.LastError = null;
        }

        await db.SaveChangesAsync(ct);
        return new WebhookResult(false, enabled ? "Webhook enabled." : "Webhook disabled.", id);
    }

    public async Task<WebhookResult> DeleteAsync(ulong guildId, Guid id, CancellationToken ct = default)
    {
        var hook = await Find(guildId, id, ct);
        if (hook is null)
        {
            return new WebhookResult(true, "Webhook not found.");
        }

        db.CurrencyWebhooks.Remove(hook);
        await db.SaveChangesAsync(ct);
        return new WebhookResult(false, "Webhook removed.");
    }

    /// <summary>Fire a signed sample delivery so an admin can confirm their endpoint receives + verifies it.</summary>
    public async Task<WebhookResult> TestSendAsync(ulong guildId, Guid id, CancellationToken ct = default)
    {
        var hook = await Find(guildId, id, ct);
        if (hook is null)
        {
            return new WebhookResult(true, "Webhook not found.");
        }

        var sample = new WebhookMovementPayload(
            guildId, 0, Guid.Empty, "TEST", 0, "Adjustment", "test", "Test delivery from Muster", null, DateTimeOffset.UtcNow);
        var result = await dispatcher.SendAsync(hook, JsonSerializer.Serialize(sample), $"test-{Guid.NewGuid()}", ct);
        CurrencyWebhookDispatcher.ApplyHealth(hook, result);
        await db.SaveChangesAsync(ct);

        return result.Ok
            ? new WebhookResult(false, $"Test delivered (HTTP {result.StatusCode}).", id)
            : new WebhookResult(true, $"Test failed: {result.Error}", id);
    }

    private Task<CurrencyWebhook?> Find(ulong guildId, Guid id, CancellationToken ct)
        => db.FindWebhookAsync(guildId, id, ct);

    private static WebhookView ToView(CurrencyWebhook w) => new(
        w.Id, w.Url, w.Enabled, w.SourceFilter, w.ConsecutiveFailures, w.LastStatusCode, w.LastError, w.LastDeliveryAt,
        !string.IsNullOrEmpty(w.Secret));

    private static bool IsValidUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
