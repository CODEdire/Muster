using Muster.Contracts;

namespace Muster.Web.Components.Pages.Quests.Models;

/// <summary>The fields shared by the post and edit quest forms. Nested under each page's form model so
/// SSR model binding maps posted values (e.g. <c>Post.Fields.Name</c>) onto these properties.</summary>
public class QuestFormModel
{
    public string? Name { get; set; }
    public string? Details { get; set; }
    public long Amount { get; set; }
    public string? Currency { get; set; }
    public QuestTier Tier { get; set; } = QuestTier.None;
    public int Capacity { get; set; } = 1;
    public DateTime? StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>The chosen quest type (admin vocab), or null. Stored as a string for SSR form binding; parse with
    /// <see cref="TypeId"/>.</summary>
    public string? QuestType { get; set; }

    /// <summary>The parsed quest-type id (null when none / unparseable).</summary>
    public Guid? TypeId => Guid.TryParse(QuestType, out var id) ? id : null;
}
