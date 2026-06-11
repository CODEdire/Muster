using Microsoft.AspNetCore.Components;

namespace Muster.Web.Components.Shared;

public partial class PageHeader
{
    /// <summary>Breadcrumb trail in order; the last entry renders as the current page (its Href is ignored).</summary>
    [Parameter, EditorRequired] public IReadOnlyList<Crumb> Crumbs { get; set; } = [];

    /// <summary>Optional Material symbol name shown left of the breadcrumb.</summary>
    [Parameter] public string? Icon { get; set; }

    /// <summary>Plain-text subtitle under the breadcrumb. Ignored when <see cref="SubtitleContent"/> is set.</summary>
    [Parameter] public string? Subtitle { get; set; }

    /// <summary>Rich subtitle content (takes precedence over <see cref="Subtitle"/>).</summary>
    [Parameter] public RenderFragment? SubtitleContent { get; set; }

    /// <summary>Optional summary tiles. ≤ <see cref="InlineKpiMax"/> render inline top-right; more render as a
    /// full-width strip below the breadcrumb (and always as a strip on mobile).</summary>
    [Parameter] public IReadOnlyList<KpiItem>? Kpis { get; set; }

    /// <summary>Optional sub-nav / action bar rendered under the breadcrumb.</summary>
    [Parameter] public RenderFragment? Toolbar { get; set; }

    /// <summary>The most KPIs shown inline (top-right) before they wrap to a full-width strip on desktop.</summary>
    private const int InlineKpiMax = 3;

    private bool HasManyKpis => Kpis is { Count: > InlineKpiMax };

    public record Crumb(string Label, string? Href = null);

    /// <summary>A single summary tile. <paramref name="Tone"/> is an optional accent (e.g. "good", "warn", "bad").</summary>
    public record KpiItem(string Value, string Label, string? Tone = null);
}
