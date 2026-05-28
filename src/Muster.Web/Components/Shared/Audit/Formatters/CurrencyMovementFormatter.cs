using System.Text.Json;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Components.Shared.Audit.Formatters;

/// <summary>
/// Formatter for the four currency-movement audit actions: Adjust, Transfer, Mint, Spend. Each shares the same
/// shape (a delta on a member wallet, optionally with a counterparty) so one formatter covers all four with the
/// same token vocabulary. The DetailSheet shows the full breakdown; the inline summary is one readable line.
/// </summary>
public class CurrencyMovementFormatter : IAuditFormatter
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> _keys =
    [
        AuditActions.Currency.Adjust.Key,
        AuditActions.Currency.Transfer.Key,
        AuditActions.Currency.Mint.Key,
        AuditActions.Currency.Spend.Key,
    ];

    public bool Matches(string action) => _keys.Contains(action);

    public AuditRender Render(AuditEntryView entry)
    {
        if (string.IsNullOrEmpty(entry.Details))
        {
            return Empty(entry);
        }

        try
        {
            return entry.Action switch
            {
                var k when k == AuditActions.Currency.Adjust.Key => RenderAdjust(entry, JsonSerializer.Deserialize<CurrencyAdjustPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.Transfer.Key => RenderTransfer(entry, JsonSerializer.Deserialize<CurrencyTransferPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.Mint.Key => RenderMint(entry, JsonSerializer.Deserialize<CurrencyMintPayload>(entry.Details, _json)),
                var k when k == AuditActions.Currency.Spend.Key => RenderSpend(entry, JsonSerializer.Deserialize<CurrencySpendPayload>(entry.Details, _json)),
                _ => Empty(entry),
            };
        }
        catch (JsonException)
        {
            // Older row still in plain-text format — let the default formatter handle it.
            return Empty(entry);
        }
    }

    // ---- Adjust (staff balance correction) ---------------------------------

    private static AuditRender RenderAdjust(AuditEntryView entry, CurrencyAdjustPayload? p)
    {
        if (p is null) return Empty(entry);

        var verb = p.Delta >= 0 ? "Awarded " : "Deducted ";
        var preposition = p.Delta >= 0 ? " to " : " from ";

        var summary = new List<AuditToken>
        {
            new TextToken(verb),
            new AmountToken(p.Delta, p.CurrencyCode),
            new TextToken(preposition),
            new UserToken(p.UserId),
        };
        AppendReasonInline(summary, p.Reason);

        return new AuditRender(summary, [Section("Adjustment",
            ("Currency", [new CurrencyToken(p.CurrencyCode)]),
            ("Amount", [new AmountToken(p.Delta, p.CurrencyCode)]),
            ("Member", [new UserToken(p.UserId)]),
            ("Reason", ReasonTokens(p.Reason)),
            ("External id", ExternalIdTokens(p.ExternalId)))]);
    }

    // ---- Transfer (member → member) ----------------------------------------

    private static AuditRender RenderTransfer(AuditEntryView entry, CurrencyTransferPayload? p)
    {
        if (p is null) return Empty(entry);

        var summary = new List<AuditToken>
        {
            new TextToken("Transferred "),
            new AmountToken(p.Amount, p.CurrencyCode),
            new TextToken(" from "),
            new UserToken(p.FromUserId),
            new TextToken(" to "),
            new UserToken(p.ToUserId),
        };
        AppendReasonInline(summary, p.Reason);

        return new AuditRender(summary, [Section("Transfer",
            ("Currency", [new CurrencyToken(p.CurrencyCode)]),
            ("Amount", [new AmountToken(p.Amount, p.CurrencyCode)]),
            ("From", [new UserToken(p.FromUserId)]),
            ("To", [new UserToken(p.ToUserId)]),
            ("Reason", ReasonTokens(p.Reason)))]);
    }

    // ---- Mint (connector inbound) ------------------------------------------

    private static AuditRender RenderMint(AuditEntryView entry, CurrencyMintPayload? p)
    {
        if (p is null) return Empty(entry);

        var summary = new List<AuditToken>
        {
            new TextToken("Minted "),
            new AmountToken(p.Amount, p.CurrencyCode),
            new TextToken(" to "),
            new UserToken(p.UserId),
        };
        AppendReasonInline(summary, p.Reason);

        return new AuditRender(summary, [Section("Mint",
            ("Currency", [new CurrencyToken(p.CurrencyCode)]),
            ("Amount", [new AmountToken(p.Amount, p.CurrencyCode)]),
            ("Member", [new UserToken(p.UserId)]),
            ("Reason", ReasonTokens(p.Reason)),
            ("External id", ExternalIdTokens(p.ExternalId)))]);
    }

    // ---- Spend (connector debit) -------------------------------------------

    private static AuditRender RenderSpend(AuditEntryView entry, CurrencySpendPayload? p)
    {
        if (p is null) return Empty(entry);

        var summary = new List<AuditToken>
        {
            new TextToken("Spent "),
            new AmountToken(-p.Amount, p.CurrencyCode),
            new TextToken(" from "),
            new UserToken(p.UserId),
        };
        AppendReasonInline(summary, p.Reason);

        return new AuditRender(summary, [Section("Spend",
            ("Currency", [new CurrencyToken(p.CurrencyCode)]),
            ("Amount", [new AmountToken(-p.Amount, p.CurrencyCode)]),
            ("Member", [new UserToken(p.UserId)]),
            ("Reason", ReasonTokens(p.Reason)),
            ("External id", ExternalIdTokens(p.ExternalId)))]);
    }

    // ---- Helpers -----------------------------------------------------------

    private static void AppendReasonInline(List<AuditToken> tokens, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        tokens.Add(new TextToken(" — "));
        tokens.Add(new TextToken(reason, AuditStyle.Muted));
    }

    private static IReadOnlyList<AuditToken> ReasonTokens(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? [new TextToken("—", AuditStyle.Muted)] : [new TextToken(reason)];

    private static IReadOnlyList<AuditToken> ExternalIdTokens(string? id) =>
        string.IsNullOrWhiteSpace(id) ? [new TextToken("—", AuditStyle.Muted)] : [new CodeToken(id)];

    /// <summary>Build a structured section from (label, value-tokens) pairs — rendered as a label/value grid.</summary>
    private static AuditSection Section(string heading, params (string Label, IReadOnlyList<AuditToken> Value)[] rows)
        => new(heading, Body: [], Rows: rows.Select(r => new AuditSectionRow(r.Label, r.Value)).ToList());

    private static AuditRender Empty(AuditEntryView entry) =>
        new(
            Summary: [new TextToken(entry.Details ?? "", AuditStyle.Muted)],
            Sections: [new AuditSection("Details", [new TextToken(entry.Details ?? "")])]);
}
