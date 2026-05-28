namespace Muster.Web;

/// <summary>
/// Discord channel kinds the shared <c>ChannelLabel</c> can render an icon for. Intentionally broader than the
/// domain's Text/Voice (<see cref="Muster.Domain.Enums.GuildChannelKind"/>) so the UI is ready as more types are
/// modelled — callers map their data onto this.
/// </summary>
public enum ChannelKind
{
    Text,
    Voice,
    Stage,
    Forum,
    Announcement,
    Category,
}
