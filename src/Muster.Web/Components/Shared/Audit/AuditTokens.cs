namespace Muster.Web.Components.Shared.Audit;

/// <summary>One styled fragment inside an audit-log line or section body. Renders pure data — formatters never
/// reach into the DB; resolution happens later via <see cref="AuditLookups"/>.</summary>
public abstract record AuditToken;

/// <summary>Plain text with optional style. Default formatter emits a single one of these for legacy details.</summary>
public record TextToken(string Text, AuditStyle Style = AuditStyle.None) : AuditToken;

/// <summary>Monospaced code-style fragment — action keys, ids, raw values.</summary>
public record CodeToken(string Text) : AuditToken;

/// <summary>A user reference by Discord id. Renders as a <c>UserChip</c> once the lookup table populates.</summary>
public record UserToken(ulong Id) : AuditToken;

/// <summary>A role reference by Discord id. Reserved for Phase B formatters (role-mapping events).</summary>
public record RoleToken(ulong Id) : AuditToken;

/// <summary>A channel reference by Discord id. Reserved for Phase B formatters (tracking config, session ops).</summary>
public record ChannelToken(ulong Id) : AuditToken;

/// <summary>A currency reference by code (e.g. <c>POINTS</c>, <c>COIN</c>).</summary>
public record CurrencyToken(string Code) : AuditToken;

/// <summary>A signed amount, optionally tagged to a currency. Auto-styles positive green / negative red.</summary>
public record AmountToken(long Value, string? CurrencyCode = null) : AuditToken;

/// <summary>An anchor with display text + href. Style flags apply to the visible text.</summary>
public record LinkToken(string Text, string Href, AuditStyle Style = AuditStyle.None) : AuditToken;

/// <summary>Inline style flags. Combine with bitwise-or. The renderer picks CSS classes per flag.</summary>
[Flags]
public enum AuditStyle
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Underline = 1 << 2,
    Muted = 1 << 3,
    Positive = 1 << 4,
    Negative = 1 << 5,
    Warning = 1 << 6,
}

/// <summary>What a formatter produces for one audit entry. <see cref="Summary"/> is the inline cell line;
/// <see cref="Sections"/> is the popup/flyout body (heading + tokens per section).</summary>
public record AuditRender(IReadOnlyList<AuditToken> Summary, IReadOnlyList<AuditSection> Sections);

/// <summary>One heading + body block inside the detail sheet. Use <see cref="Rows"/> for a clean
/// label / value table (typical for structured payloads); use <see cref="Body"/> when the section is just a
/// stream of text/code (the default formatter's "Payload" or "Details" sections). Exactly one of the two is set.</summary>
public record AuditSection(string Heading, IReadOnlyList<AuditToken> Body, IReadOnlyList<AuditSectionRow>? Rows = null);

/// <summary>A single label + value row inside a structured section. Renders as a two-column grid in the sheet.</summary>
public record AuditSectionRow(string Label, IReadOnlyList<AuditToken> Value);
