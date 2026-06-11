using Microsoft.AspNetCore.Components;
using Muster.Infrastructure.Services.Quests;
using Muster.Web.Components.Pages.Quests.Models;

namespace Muster.Web.Components.Pages.Quests.Components;

public partial class QuestCard
{
    [Parameter, EditorRequired] public QuestBoardItem Quest { get; set; } = default!;

    /// <summary>Per-board lookup bundle (codes, names, avatars, types, settings, role, zone).</summary>
    [Parameter, EditorRequired] public QuestCardData Data { get; set; } = default!;

    /// <summary>The role-gated action buttons for this quest. The board page owns the logic + the shared action
    /// modal; the card only renders the fragment in its action bar.</summary>
    [Parameter] public RenderFragment<QuestBoardItem>? Actions { get; set; }

    /// <summary>Whether this quest has any available action (the page computes it from ActionsFor + Can* gates).
    /// Keeps the action bar from reserving space on cards with nothing to do.</summary>
    [Parameter] public bool ShowActions { get; set; }
}
