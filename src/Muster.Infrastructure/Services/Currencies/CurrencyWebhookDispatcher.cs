using System.Text;
using Muster.Domain.Entities.Currencies;
using Muster.Domain.Enums;
using Muster.Infrastructure.Connectors;

namespace Muster.Infrastructure.Services.Currencies;

/// <summary>The JSON body POSTed to a webhook for one currency movement.</summary>
public record WebhookMovementPayload(
    ulong GuildId, ulong UserId, Guid CurrencyId, string CurrencyCode, long Amount,
    string Source, string? SourceId, string Reason, Guid? SeasonId, DateTimeOffset OccurredAt);

/// <summary>The result of one delivery attempt (for health tracking).</summary>
public record WebhookDelivery(bool Ok, int? StatusCode, string? Error);

/// <summary>
/// Delivers a movement payload to one webhook endpoint: an HMAC-SHA256-signed POST (header <c>X-Muster-Signature:
/// sha256=…</c>, reusing the connector's signer), with an idempotency <c>X-Muster-Delivery</c> header so subscribers
/// can dedupe a redelivery. Best-effort — never throws; the caller applies the result to the webhook's health and
/// auto-disables it after too many consecutive failures (mirroring the connector).
/// </summary>
public sealed class CurrencyWebhookDispatcher(HttpClient http, IConnectorSecretProtector secrets)
{
    /// <summary>Auto-disable a webhook after this many consecutive failed deliveries.</summary>
    public const int DisableAfterFailures = 15;

    public async Task<WebhookDelivery> SendAsync(CurrencyWebhook hook, string jsonBody, string deliveryId, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, hook.Url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };

            var secret = secrets.Unprotect(hook.Secret);
            if (!string.IsNullOrEmpty(secret))
            {
                req.Headers.TryAddWithoutValidation("X-Muster-Signature", CurrencyConnectorClient.Sign(ConnectorSignAlgorithm.HmacSha256, secret, jsonBody));
            }

            req.Headers.TryAddWithoutValidation("X-Muster-Delivery", deliveryId);
            req.Headers.TryAddWithoutValidation("X-Muster-Event", "currency.movement");

            using var resp = await http.SendAsync(req, ct);
            var code = (int)resp.StatusCode;
            var ok = code is >= 200 and < 300;
            return new WebhookDelivery(ok, code, ok ? null : $"HTTP {code}");
        }
        catch (Exception ex)
        {
            return new WebhookDelivery(false, null, ex.Message);
        }
    }

    /// <summary>Fold a delivery result into the webhook's health, auto-disabling after the failure threshold. Pure
    /// (no IO) so the caller controls persistence and it's unit-testable.</summary>
    public static void ApplyHealth(CurrencyWebhook hook, WebhookDelivery result)
    {
        hook.LastDeliveryAt = DateTimeOffset.UtcNow;
        hook.LastStatusCode = result.StatusCode;
        if (result.Ok)
        {
            hook.ConsecutiveFailures = 0;
            hook.LastError = null;
        }
        else
        {
            hook.ConsecutiveFailures++;
            hook.LastError = result.Error;
            if (hook.ConsecutiveFailures >= DisableAfterFailures)
            {
                hook.Enabled = false;
            }
        }
    }
}
