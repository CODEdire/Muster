using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Currencies;

/// <summary>
/// A per-guild outbound webhook subscription: every staged currency movement (<c>CurrencyMovementRecorded</c>) is
/// POSTed to <see cref="Url"/>, HMAC-signed with <see cref="Secret"/> (encrypted at rest). <see cref="SourceFilter"/>
/// optionally narrows which movement sources fire it (empty = all). Delivery is best-effort with health tracking:
/// consecutive failures accrue and the subscription auto-disables past a threshold, mirroring the connector's
/// resilience. Managed by guild admins; separate from API keys (which are inbound-only).
/// </summary>
public class CurrencyWebhook
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Absolute HTTPS endpoint the movement payload is POSTed to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 key for the <c>X-Muster-Signature</c> header. Stored encrypted; write-only in the UI.</summary>
    public string? Secret { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Movement sources that fire this webhook; empty = every source (checkpoints never publish, so they're
    /// excluded regardless).</summary>
    public List<CurrencyLedgerSource> SourceFilter { get; set; } = [];

    // --- Delivery health (mirrors the connector) ---
    public int ConsecutiveFailures { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastDeliveryAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>True when this webhook should receive the given movement source (empty filter = all).</summary>
    public bool Matches(CurrencyLedgerSource source) => SourceFilter.Count == 0 || SourceFilter.Contains(source);
}
