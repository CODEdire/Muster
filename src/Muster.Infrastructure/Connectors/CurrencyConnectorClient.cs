using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Connectors;

/// <summary>The movement (or balance probe) a connector action runs against. Also the placeholder source for
/// <see cref="ConnectorTemplate"/>. <see cref="IdempotencyKey"/> (when set) is the value sent in the connector's
/// idempotency header so a retried delivery dedupes on the receiver; it should be stable across retries of the
/// same logical movement (e.g. the ledger source key). Falls back to <see cref="DeliveryId"/> when null.</summary>
public record ConnectorDispatch(
    ulong GuildId, string CurrencyCode, ulong UserId, long Amount, string Reason,
    string SourceType, DateTimeOffset OccurredAt, long DeliveryId, string? DisplayName = null, string? IdempotencyKey = null);

/// <summary>Which configured action to run.</summary>
public enum ConnectorActionKind { Credit, Debit, GetBalance }

/// <summary>Outcome of a connector call. <see cref="Balance"/> is set only for a successful GetBalance.
/// <see cref="Configured"/> is false when the action simply isn't set up (vs an actual call that failed) — the
/// synchronous funnel treats "not configured" as "nothing to push" rather than an abort.</summary>
public record ConnectorResult(bool Success, int StatusCode, string? Error, long? Balance, bool Configured = true)
{
    public static ConnectorResult NotConfigured(ConnectorActionKind kind) => new(false, 0, $"{kind} action is not configured.", null, Configured: false);
}

/// <summary>Thrown when a synchronous external connector call fails, aborting the currency operation before commit.</summary>
public sealed class ConnectorException(string message) : Exception(message);

/// <summary>Outbound connector calls (testable seam over <see cref="CurrencyConnectorClient"/>).</summary>
public interface ICurrencyConnectorClient
{
    Task<ConnectorResult> ExecuteAsync(CurrencyConnector cfg, ConnectorActionKind kind, ConnectorDispatch ctx, CancellationToken ct = default);
    Task<long?> GetBalanceAsync(CurrencyConnector cfg, ConnectorDispatch ctx, CancellationToken ct = default);
}

/// <summary>Encrypts/decrypts connector secrets at rest (Data Protection). Implementations no-op on empty input.</summary>
public interface IConnectorSecretProtector
{
    string? Protect(string? plaintext);
    string? Unprotect(string? ciphertext);
}

public sealed class ConnectorSecretProtector(IDataProtectionProvider provider) : IConnectorSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("currency-connector.secret");

    public string? Protect(string? plaintext) => string.IsNullOrEmpty(plaintext) ? plaintext : _protector.Protect(plaintext);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return ciphertext;
        }

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            return null; // tampered or encrypted under a lost key
        }
    }
}

/// <summary>
/// Renders a connector action's path/body template against a movement. Tokens use a <c>$name</c> syntax (no braces,
/// so a body template stays <b>valid JSON</b> and highlights cleanly). <b>Numeric</b> tokens
/// (<c>$userId $amount $guildId $deliveryId</c>) render as raw numbers — write them quoted in the template
/// (<c>"$amount"</c>) and the quotes are stripped, so the template parses as JSON yet emits a number. <b>String</b>
/// tokens (<c>$displayName $currencyCode $reason $sourceType $occurredAt</c>) render as JSON-escaped content inside
/// the template's quotes. Unknown <c>$…</c> text is left as-is.
/// </summary>
public static class ConnectorTemplate
{
    public static string Render(string template, ConnectorDispatch m)
    {
        string Str(string? s) => JsonEncodedText.Encode(s ?? string.Empty).ToString();
        var userId = m.UserId.ToString();
        var amount = m.Amount.ToString(CultureInfo.InvariantCulture);
        var guildId = m.GuildId.ToString();
        var deliveryId = m.DeliveryId.ToString();

        return template
            // Quoted numeric form first — keeps the template valid JSON: "$amount" → 50 (a JSON number).
            .Replace("\"$userId\"", userId)
            .Replace("\"$amount\"", amount)
            .Replace("\"$guildId\"", guildId)
            .Replace("\"$deliveryId\"", deliveryId)
            // Bare numeric (author wrote it unquoted).
            .Replace("$userId", userId)
            .Replace("$amount", amount)
            .Replace("$guildId", guildId)
            .Replace("$deliveryId", deliveryId)
            // String tokens: JSON-escaped content placed inside the template's own quotes.
            .Replace("$displayName", Str(m.DisplayName))
            .Replace("$currencyCode", Str(m.CurrencyCode))
            .Replace("$reason", Str(m.Reason))
            .Replace("$sourceType", Str(m.SourceType))
            .Replace("$occurredAt", Str(m.OccurredAt.ToString("o")));
    }

    /// <summary>Render placeholders for a URL path or query string: numbers raw, string values URL-encoded.</summary>
    public static string RenderUrl(string template, ConnectorDispatch m) => template
        .Replace("$userId", m.UserId.ToString())
        .Replace("$amount", m.Amount.ToString(CultureInfo.InvariantCulture))
        .Replace("$guildId", m.GuildId.ToString())
        .Replace("$deliveryId", m.DeliveryId.ToString())
        .Replace("$currencyCode", Uri.EscapeDataString(m.CurrencyCode))
        .Replace("$displayName", Uri.EscapeDataString(m.DisplayName ?? string.Empty))
        .Replace("$reason", Uri.EscapeDataString(m.Reason))
        .Replace("$sourceType", Uri.EscapeDataString(m.SourceType))
        .Replace("$occurredAt", Uri.EscapeDataString(m.OccurredAt.ToString("o")));
}

/// <summary>
/// One configurable HTTP client for a currency's external economy API: builds a request from a
/// <see cref="ConnectorAction"/> (URL, method, static + auth + idempotency + signature headers, rendered body),
/// sends it with the connector's timeout, and judges success by the configured codes. <see cref="GetBalanceAsync"/>
/// also parses the balance out of the response. Secrets are decrypted only here, at send time.
/// </summary>
public sealed class CurrencyConnectorClient(
    IHttpClientFactory http, IConnectorSecretProtector secrets, ILogger<CurrencyConnectorClient> logger) : ICurrencyConnectorClient
{
    public const string ClientName = "currency-connector";

    /// <summary>Stamp a connector's last-delivery health from a result (for the admin diagnostics view).</summary>
    public static void RecordHealth(CurrencyConnector cfg, ConnectorResult r)
    {
        cfg.LastAttemptAt = DateTimeOffset.UtcNow;
        cfg.LastStatus = r.Success ? "ok" : r.StatusCode > 0 ? $"HTTP {r.StatusCode}" : "error";
        cfg.LastError = r.Success ? null : r.Error;
    }

    /// <summary>Gate (pure) for whether a ledger movement should be pushed at all.</summary>
    public static bool ShouldDispatch(CurrencyMode mode, CurrencyConnector? cfg, CurrencyLedgerSource sourceType)
        => mode is CurrencyMode.External or CurrencyMode.Hybrid
           && cfg is { Enabled: true }
           && sourceType != CurrencyLedgerSource.Connector; // loop guard: never echo an external-origin movement back

    public Task<ConnectorResult> ExecuteAsync(CurrencyConnector cfg, ConnectorActionKind kind, ConnectorDispatch ctx, CancellationToken ct = default)
    {
        cfg.Normalize(); // tolerate connectors stored before the full graph existed
        var action = kind switch
        {
            ConnectorActionKind.Credit => cfg.Credit,
            ConnectorActionKind.Debit => cfg.Debit,
            _ => cfg.GetBalance,
        };
        return SendAsync(cfg, action, kind, ctx, ct);
    }

    /// <summary>Read the external balance via the GetBalance action; null when not configured or on failure.</summary>
    public async Task<long?> GetBalanceAsync(CurrencyConnector cfg, ConnectorDispatch ctx, CancellationToken ct = default)
        => (await ExecuteAsync(cfg, ConnectorActionKind.GetBalance, ctx, ct)).Balance;

    private async Task<ConnectorResult> SendAsync(
        CurrencyConnector cfg, ConnectorAction action, ConnectorActionKind kind, ConnectorDispatch ctx, CancellationToken ct)
    {
        if (!action.Enabled)
        {
            return ConnectorResult.NotConfigured(kind);
        }

        var url = BuildUrl(cfg, action, ctx);
        if (string.IsNullOrWhiteSpace(url))
        {
            return ConnectorResult.NotConfigured(kind);
        }

        using var req = new HttpRequestMessage(new HttpMethod((action.Method ?? "POST").ToUpperInvariant()), url);

        string? body = null;
        if (!string.IsNullOrWhiteSpace(action.BodyTemplate))
        {
            // Form bodies render URL-encoded (a=1&b=2); JSON bodies use the quoted-token template.
            var isForm = action.BodyFormat == ConnectorBodyFormat.Form;
            body = isForm ? ConnectorTemplate.RenderUrl(action.BodyTemplate, ctx) : ConnectorTemplate.Render(action.BodyTemplate, ctx);
            req.Content = new StringContent(body, Encoding.UTF8, isForm ? "application/x-www-form-urlencoded" : "application/json");
        }

        foreach (var h in cfg.Headers)
        {
            req.Headers.TryAddWithoutValidation(h.Name, h.Value);
        }

        if (!string.IsNullOrWhiteSpace(cfg.IdempotencyHeader))
        {
            // Stable key (the ledger source) when available, so resilience-handler retries dedupe on the receiver.
            req.Headers.TryAddWithoutValidation(cfg.IdempotencyHeader, ctx.IdempotencyKey ?? ctx.DeliveryId.ToString());
        }

        ApplyAuth(cfg.Auth, req);
        ApplySigning(cfg.Signing, req, body);

        // Per-connector timeout bounds the whole call (incl. resilience-handler retries); HttpClient.Timeout is
        // disabled (set to infinite in DI) so this CTS governs.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds <= 0 ? 10 : cfg.TimeoutSeconds));

        try
        {
            var client = http.CreateClient(ClientName);
            using var res = await client.SendAsync(req, cts.Token);
            var code = (int)res.StatusCode;
            var responseText = await res.Content.ReadAsStringAsync(cts.Token);

            if (!IsSuccess(cfg.SuccessCodes, code))
            {
                var detail = ExtractError(cfg.ErrorPath, responseText) ?? $"HTTP {code}";
                logger.LogWarning("Connector {Kind} for {Code} → HTTP {Status}: {Detail}", kind, ctx.CurrencyCode, code, detail);
                return new ConnectorResult(false, code, detail, null);
            }

            // Any action may return a value (the resulting balance) — capture it for reconciliation.
            var balance = ParseResponseValue(action.ResponseFormat, action.ResponsePath, responseText);
            return new ConnectorResult(true, code, null, balance);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            var why = cts.IsCancellationRequested && !ct.IsCancellationRequested ? "timed out" : ex.Message;
            logger.LogWarning(ex, "Connector {Kind} for {Code} failed: {Why}", kind, ctx.CurrencyCode, why);
            return new ConnectorResult(false, 0, why, null);
        }
    }

    /// <summary>Extract a human-readable error from a response body (JSON path if configured, else a truncated snippet).</summary>
    private static string? ExtractError(string? errorPath, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(errorPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var cur = doc.RootElement;
                foreach (var seg in errorPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur))
                    {
                        cur = default;
                        break;
                    }
                }

                if (cur.ValueKind == JsonValueKind.String)
                {
                    return cur.GetString();
                }
            }
            catch (JsonException)
            {
                // fall through to the snippet
            }
        }

        var trimmed = body.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }

    private string? BuildUrl(CurrencyConnector cfg, ConnectorAction action, ConnectorDispatch ctx)
    {
        var path = string.IsNullOrWhiteSpace(action.Path) ? null : ConnectorTemplate.RenderUrl(action.Path, ctx);
        var baseUrl = cfg.BaseUrl?.TrimEnd('/');

        var url = (path, baseUrl) switch
        {
            (null, { } b) => b,
            ({ } p, _) when Uri.IsWellFormedUriString(p, UriKind.Absolute) => p,
            ({ } p, { } b) => $"{b}/{p.TrimStart('/')}",
            ({ } p, null) => p,
            _ => null,
        };
        if (url is null)
        {
            return null;
        }

        // Configured query string (templated), then an API-key carried in the query (decrypted here).
        if (!string.IsNullOrWhiteSpace(action.Query))
        {
            url += (url.Contains('?') ? '&' : '?') + ConnectorTemplate.RenderUrl(action.Query.TrimStart('?', '&'), ctx);
        }

        if (cfg.Auth is { Scheme: ConnectorAuthScheme.ApiKey, ApiKeyIn: ApiKeyLocation.Query })
        {
            var key = secrets.Unprotect(cfg.Auth.Secret) ?? string.Empty;
            url += (url.Contains('?') ? '&' : '?') + $"{Uri.EscapeDataString(cfg.Auth.ApiKeyName)}={Uri.EscapeDataString(key)}";
        }

        return url;
    }

    private void ApplyAuth(ConnectorAuth auth, HttpRequestMessage req)
    {
        var secret = secrets.Unprotect(auth.Secret);
        switch (auth.Scheme)
        {
            case ConnectorAuthScheme.Basic:
                var raw = $"{auth.Username}:{secret}";
                req.Headers.TryAddWithoutValidation("Authorization", $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))}");
                break;
            case ConnectorAuthScheme.Bearer:
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
                break;
            case ConnectorAuthScheme.ApiKey when auth.ApiKeyIn == ApiKeyLocation.Header:
                req.Headers.TryAddWithoutValidation(auth.ApiKeyName, secret);
                break;
            // ApiKey-in-query is applied to the URL in BuildUrl; None adds nothing.
        }
    }

    private void ApplySigning(ConnectorSigning signing, HttpRequestMessage req, string? body)
    {
        if (signing.Algorithm == ConnectorSignAlgorithm.None)
        {
            return;
        }

        var key = secrets.Unprotect(signing.Secret);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var payload = body ?? string.Empty;
        string? timestamp = null;
        if (!string.IsNullOrWhiteSpace(signing.TimestampHeader))
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            payload = $"{timestamp}.{payload}";
            req.Headers.TryAddWithoutValidation(signing.TimestampHeader, timestamp);
        }

        req.Headers.TryAddWithoutValidation(signing.SignatureHeader, Sign(signing.Algorithm, key, payload));
    }

    /// <summary>The signature header value for a payload — pure, so it's unit-testable and reusable.</summary>
    public static string Sign(ConnectorSignAlgorithm algorithm, string key, string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var data = Encoding.UTF8.GetBytes(payload);
        var (prefix, hash) = algorithm switch
        {
            ConnectorSignAlgorithm.HmacSha512 => ("sha512", HMACSHA512.HashData(keyBytes, data)),
            _ => ("sha256", HMACSHA256.HashData(keyBytes, data)),
        };
        return $"{prefix}={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static bool IsSuccess(string? successCodes, int code)
    {
        if (string.IsNullOrWhiteSpace(successCodes))
        {
            return code is >= 200 and < 300;
        }

        foreach (var part in successCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var c) && c == code)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Read a numeric value from a response body — JSON path, whole text, or a regex capture. Pure, testable.</summary>
    public static long? ParseResponseValue(ConnectorResponseFormat format, string? path, string body)
    {
        switch (format)
        {
            case ConnectorResponseFormat.Text:
                return long.TryParse(body.Trim(), out var n) ? n : null;

            case ConnectorResponseFormat.Json when !string.IsNullOrWhiteSpace(path):
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    return ReadBalance(doc.RootElement, path);
                }
                catch (JsonException)
                {
                    return null;
                }

            case ConnectorResponseFormat.Regex when !string.IsNullOrWhiteSpace(path):
                var match = System.Text.RegularExpressions.Regex.Match(body, path);
                return match.Success && match.Groups.Count > 1 && long.TryParse(match.Groups[1].Value, out var r) ? r : null;

            default:
                return null;
        }
    }

    /// <summary>Walk a dotted path (e.g. <c>data.amount</c>) to a numeric balance — pure, unit-testable.</summary>
    public static long? ReadBalance(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Number when current.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(current.GetString(), out var s) => s,
            _ => null,
        };
    }
}
