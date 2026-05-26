using Muster.Contracts;

namespace Muster.Web;

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
}
