using Microsoft.AspNetCore.Components;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;

namespace Muster.Web.Components.Pages.Quests.Components;

public partial class QuestHero
{
    [Parameter, EditorRequired] public QuestDetailView Quest { get; set; } = default!;
    [Parameter, EditorRequired] public ulong GuildId { get; set; }

    /// <summary>Staff (admin / economy manager) — links the issuer chip to the member-detail page.</summary>
    [Parameter] public bool IsStaff { get; set; }

    private bool IsPlayer => Quest.Origin == QuestOrigin.Player;

    private string RewardClass =>
        Quest.RewardCode.Equals("POINTS", StringComparison.OrdinalIgnoreCase) ? "reward-points" : "reward-coin";

    /// <summary>Relative deadline text + an urgency tone ("warn" under a day, "bad" overdue).</summary>
    private (string Text, string? Tone) Due()
    {
        if (Quest.Status is QuestStatus.Closed or QuestStatus.Cancelled or QuestStatus.Expired)
        {
            return ("closed", null);
        }

        if (Quest.Deadline is not { } d)
        {
            return ("no expiry", null);
        }

        var span = d - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero)
        {
            return ("overdue", "bad");
        }

        return span.TotalHours < 24
            ? ($"{(int)Math.Max(1, span.TotalHours)}h left", "warn")
            : ($"{(int)span.TotalDays}d left", null);
    }
}
