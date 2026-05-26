using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Muster.Web;

/// <summary>
/// Renders Discord-flavored markdown to safe HTML for the web. User input is treated as markdown only:
/// raw HTML is disabled (so no tag injection), and dangerous link schemes are neutralised after render.
/// </summary>
public static partial class DiscordMarkdown
{
    // DisableHtml: any literal HTML in the source is escaped, not passed through. Emphasis extras add
    // ~~strike~~ / __underline__-ish handling; auto-links turn bare URLs into links.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>Render markdown to sanitized HTML (raw HTML disabled, unsafe link schemes stripped).</summary>
    public static MarkupString ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString(string.Empty);
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        html = UnsafeScheme().Replace(html, "href=\"#\"");
        return new MarkupString(html);
    }

    /// <summary>A plain-text, single-line snippet for cards: strips markdown, collapses whitespace, truncates.</summary>
    public static string ToSnippet(string? markdown, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = Markdown.ToPlainText(markdown, Pipeline);
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    [GeneratedRegex(@"href=""\s*(?:javascript|data|vbscript):[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeScheme();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
