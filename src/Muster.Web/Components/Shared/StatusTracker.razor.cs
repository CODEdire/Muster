using Microsoft.AspNetCore.Components;

namespace Muster.Web.Components.Shared;

public partial class StatusTracker
{
    /// <summary>Ordered stage labels, left → right.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<string> Stages { get; set; } = [];

    /// <summary>Index (0-based) of the current stage. Stages at or before it render as reached.</summary>
    [Parameter] public int Current { get; set; }

    /// <summary>Optional per-stage counts (parallel to <see cref="Stages"/>); a positive value renders a badge on
    /// that stage. Use null entries (or a null list) for stages without a count.</summary>
    [Parameter] public IReadOnlyList<int?>? Counts { get; set; }

    /// <summary>Optional tone for the reached run (e.g. "danger" for a cancelled/disputed terminal state).</summary>
    [Parameter] public string? Tone { get; set; }
}
