using Muster.Domain.Enums;

namespace Muster.Domain.Entities.Currencies;

/// <summary>
/// A guild-scoped currency. The seeded "POINTS" currency is seasonal and drives leaderboards;
/// spendable currencies (e.g. "COIN") persist as a wallet and are mintable/spendable by connectors.
/// </summary>
public class Currency
{
    public Guid Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Stable code, e.g. POINTS or COIN.</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public bool IsSeasonal { get; set; }
    public bool IsSpendable { get; set; }

    /// <summary>Whether members may transfer this currency to each other (member-to-member sends). Seasonal score
    /// currencies (e.g. POINTS) are typically non-transferable so standing can't be gifted.</summary>
    public bool IsTransferable { get; set; }

    /// <summary>The guild's default/primary currency — the one a member's wallet opens on and the default send
    /// currency. At most one currency per guild has this set (enforced in the admin service).</summary>
    public bool IsPrimary { get; set; }

    /// <summary>Where balance authority lives for this currency (see <see cref="CurrencyMode"/>).</summary>
    public CurrencyMode Mode { get; set; } = CurrencyMode.Internal;

    /// <summary>Outbound connector that mirrors this currency's ledger movements to a backing system. Only
    /// meaningful for External/Hybrid currencies; stored as a JSON document on the row.</summary>
    public CurrencyConnector Connector { get; set; } = new();
}

/// <summary>
/// Per-currency outbound connector (owned JSON on <see cref="Currency"/>): one HTTP client config = shared auth +
/// orthogonal signing + independently-configured <see cref="Credit"/>/<see cref="Debit"/>/<see cref="GetBalance"/>
/// actions. Lets a currency mirror an arbitrary external economy API and read its balance for reconciliation.
/// Only meaningful for External/Hybrid currencies.
/// </summary>
public class CurrencyConnector
{
    /// <summary>Master on/off — keep settings but pause delivery.</summary>
    public bool Enabled { get; set; }

    /// <summary>Optional base URL; an action's <see cref="ConnectorAction.Path"/> may be relative to this or absolute.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Per-request timeout (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>CSV of HTTP status codes treated as success (e.g. <c>200,201,204</c>). Blank = any 2xx.</summary>
    public string? SuccessCodes { get; set; }

    /// <summary>Header the per-movement delivery id is sent in (receiver dedupes on it). Blank = not sent.</summary>
    public string? IdempotencyHeader { get; set; }

    /// <summary>Static extra headers applied to every request (tenant id, API version, …).</summary>
    public List<ConnectorHeader> Headers { get; set; } = [];

    /// <summary>Optional dotted JSON path into an error response to a human-readable message (for diagnostics).</summary>
    public string? ErrorPath { get; set; }

    /// <summary>How often the periodic sweep reconciles each member's balance from the external system (minutes;
    /// 0 = no periodic sweep). The dashboard's on-visit sync uses a fixed 5-minute throttle regardless.</summary>
    public int SyncIntervalMinutes { get; set; }

    // --- Health (last delivery outcome, for the admin page) ---

    /// <summary>Outcome of the most recent connector call (e.g. <c>ok</c>, <c>HTTP 502</c>).</summary>
    public string? LastStatus { get; set; }

    /// <summary>Error detail of the most recent failed call (null when the last call succeeded).</summary>
    public string? LastError { get; set; }

    /// <summary>When the most recent call was attempted.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    public ConnectorAuth Auth { get; set; } = new();
    public ConnectorSigning Signing { get; set; } = new();

    /// <summary>Request run when a ledger movement is positive (a credit/mint to the external system).</summary>
    public ConnectorAction Credit { get; set; } = new();

    /// <summary>Request run when a ledger movement is negative (a debit/spend) — may differ from <see cref="Credit"/>.</summary>
    public ConnectorAction Debit { get; set; } = new();

    /// <summary>Request that reads the external balance, for reconciliation.</summary>
    public ConnectorAction GetBalance { get; set; } = new();

    /// <summary>Replace any null sub-objects with empty defaults. Needed because EF materializes owned-JSON nested
    /// objects as null when the stored document predates them (e.g. a connector saved before these fields existed).</summary>
    public CurrencyConnector Normalize()
    {
        Auth ??= new ConnectorAuth();
        Signing ??= new ConnectorSigning();
        Credit ??= new ConnectorAction();
        Debit ??= new ConnectorAction();
        GetBalance ??= new ConnectorAction();
        Headers ??= [];
        return this;
    }
}

/// <summary>A static header sent on every connector request.</summary>
public class ConnectorHeader
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>Connector auth. <see cref="Secret"/> is encrypted at rest (Data Protection).</summary>
public class ConnectorAuth
{
    public ConnectorAuthScheme Scheme { get; set; } = ConnectorAuthScheme.None;

    /// <summary>Basic auth username (the password goes in <see cref="Secret"/>).</summary>
    public string? Username { get; set; }

    /// <summary>Basic password / Bearer token / ApiKey value. Stored encrypted; write-only in the UI.</summary>
    public string? Secret { get; set; }

    /// <summary>ApiKey scheme: the header or query-param name the key is sent under.</summary>
    public string ApiKeyName { get; set; } = "X-Api-Key";

    /// <summary>ApiKey scheme: whether the key goes in a header or the query string.</summary>
    public ApiKeyLocation ApiKeyIn { get; set; } = ApiKeyLocation.Header;
}

/// <summary>Optional HMAC signing of the request body, independent of auth. <see cref="Secret"/> is encrypted at rest.</summary>
public class ConnectorSigning
{
    public ConnectorSignAlgorithm Algorithm { get; set; } = ConnectorSignAlgorithm.None;

    /// <summary>Header the signature is sent in.</summary>
    public string SignatureHeader { get; set; } = "X-Muster-Signature";

    /// <summary>When set, sign <c>{timestamp}.{body}</c> and send the timestamp in this header (replay protection).</summary>
    public string? TimestampHeader { get; set; }

    /// <summary>HMAC key. Stored encrypted; write-only in the UI.</summary>
    public string? Secret { get; set; }
}

/// <summary>One configurable HTTP action (credit/debit/get-balance) on a connector. All three may send a body and
/// parse a value (e.g. an updated balance) out of the response.</summary>
public class ConnectorAction
{
    public bool Enabled { get; set; }

    /// <summary>HTTP method (GET/POST/PUT/PATCH).</summary>
    public string Method { get; set; } = "POST";

    /// <summary>Request path (relative to <see cref="CurrencyConnector.BaseUrl"/> or absolute); supports placeholders.</summary>
    public string? Path { get; set; }

    /// <summary>Query-string template appended to the URL (e.g. <c>user=$userId&amp;amount=$amount</c>); tokens are
    /// substituted and string values URL-encoded.</summary>
    public string? Query { get; set; }

    /// <summary>Body template with placeholders (rendered per movement). Empty for bodyless requests. Interpreted
    /// per <see cref="BodyFormat"/> — JSON (quoted tokens) or form (<c>a=$x&amp;b=$y</c>).</summary>
    public string? BodyTemplate { get; set; }

    /// <summary>How the body is encoded/sent.</summary>
    public ConnectorBodyFormat BodyFormat { get; set; } = ConnectorBodyFormat.Json;

    /// <summary>How to read a value (the resulting balance) from the response — JSON path, plain text, or not at all.</summary>
    public ConnectorResponseFormat ResponseFormat { get; set; } = ConnectorResponseFormat.None;

    /// <summary>For <see cref="ConnectorResponseFormat.Json"/>: dotted path to the value (e.g. <c>data.amount</c>).
    /// A captured value is the member's external balance, used to reconcile the shadow wallet.</summary>
    public string? ResponsePath { get; set; }
}
