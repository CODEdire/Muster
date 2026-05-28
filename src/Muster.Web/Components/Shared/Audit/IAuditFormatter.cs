using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Components.Shared.Audit;

/// <summary>
/// Produces the token representation of an audit entry. Multiple formatters are registered into DI; the
/// registry picks the first that matches an action and falls back to <see cref="DefaultAuditFormatter"/>.
/// Formatters are pure and synchronous — entity resolution happens in <see cref="AuditLookups"/>.
/// </summary>
public interface IAuditFormatter
{
    /// <summary>True when this formatter handles the given action key (exact or prefix).</summary>
    bool Matches(string action);

    /// <summary>Render the entry to summary + section tokens.</summary>
    AuditRender Render(AuditEntryView entry);
}

/// <summary>Resolves the best formatter for an audit entry. Registered as scoped so the same DI scope drives
/// per-page lookups too.</summary>
public class AuditFormatterRegistry(IEnumerable<IAuditFormatter> formatters, DefaultAuditFormatter fallback)
{
    private readonly IReadOnlyList<IAuditFormatter> _formatters = formatters.Where(f => f is not DefaultAuditFormatter).ToList();

    public AuditRender Render(AuditEntryView entry)
    {
        foreach (var f in _formatters)
        {
            if (f.Matches(entry.Action))
            {
                return f.Render(entry);
            }
        }

        return fallback.Render(entry);
    }
}

/// <summary>Catch-all formatter. Emits the legacy <c>Details</c> string as a single TextToken in both the summary
/// and the sheet. Detects when <c>Details</c> looks like JSON and pretty-prints it for the sheet body, so legacy
/// rows still read clearly while we migrate per-action formatters in.</summary>
public class DefaultAuditFormatter : IAuditFormatter
{
    public bool Matches(string action) => true;

    public AuditRender Render(AuditEntryView entry)
    {
        var details = entry.Details ?? "";
        var trimmed = details.TrimStart();
        var looksJson = trimmed.StartsWith('{') || trimmed.StartsWith('[');

        if (looksJson)
        {
            string pretty;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(details);
                pretty = System.Text.Json.JsonSerializer.Serialize(
                    doc.RootElement,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                pretty = details;
            }

            // Inline summary is muted + truncated, full payload is the section body.
            var inline = Truncate(details, 120);
            return new AuditRender(
                Summary: [new TextToken(inline, AuditStyle.Muted)],
                Sections: [new AuditSection("Payload", [new CodeToken(pretty)])]);
        }

        return new AuditRender(
            Summary: [new TextToken(details)],
            Sections: [new AuditSection("Details", [new TextToken(details)])]);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
