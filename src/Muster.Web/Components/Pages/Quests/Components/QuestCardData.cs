using Muster.Domain.Entities.Guilds;
using Muster.Domain.Entities.Quests;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Components.Pages.Quests.Components;

/// <summary>The per-board lookup bundle a <see cref="QuestCard"/> (and the table view) need to render a quest:
/// currency codes, member names/avatars, quest types, settings, viewer role, and time zone. Built once per board
/// load and passed to every card, so the card stays pure presentation with no service calls of its own.</summary>
public sealed record QuestCardData(
    ulong GuildId,
    bool IsManager,
    bool IsStaff,
    string ZoneId,
    GuildQuestSettings Settings,
    IReadOnlyDictionary<Guid, string> Codes,
    IReadOnlyDictionary<ulong, string> Names,
    IReadOnlyDictionary<ulong, string> Avatars,
    IReadOnlyDictionary<Guid, QuestType> TypeById)
{
    /// <summary>A safe placeholder before the first board load (no guild, empty lookups).</summary>
    public static QuestCardData Empty { get; } = new(
        0, false, false, TimeZoneService.Utc, new GuildQuestSettings(),
        new Dictionary<Guid, string>(), new Dictionary<ulong, string>(),
        new Dictionary<ulong, string>(), new Dictionary<Guid, QuestType>());

    public string Name(ulong userId) => Names.GetValueOrDefault(userId, $"member {userId}");

    public string? Avatar(ulong userId) => Avatars.GetValueOrDefault(userId);

    public string Initial(ulong userId) => Name(userId) is { Length: > 0 } n ? n[..1].ToUpperInvariant() : "?";

    public string Code(Guid currencyId) => Codes.GetValueOrDefault(currencyId, "?");

    /// <summary>Member-detail link for staff (admins/economy managers); null otherwise so the chip isn't linked.</summary>
    public string? MemberHref(ulong userId) => IsStaff ? MusterLinks.MemberDetail(GuildId, userId) : null;

    /// <summary>The Material icon shown as a quest's visual — the assigned type's icon, or a neutral default.</summary>
    public string TypeIcon(Guid? typeId)
        => typeId is { } id && TypeById.TryGetValue(id, out var t) && !string.IsNullOrEmpty(t.Icon) ? t.Icon! : "assignment";

    public string? TypeName(Guid? typeId)
        => typeId is { } id && TypeById.TryGetValue(id, out var t) ? t.Name : null;

    /// <summary>Friendly deadline text for the card's meta row.</summary>
    public string DueText(QuestBoardItem q)
    {
        if (q.Status is QuestStatus.Closed or QuestStatus.Cancelled or QuestStatus.Expired)
        {
            return "Closed";
        }

        if (q.Status == QuestStatus.Scheduled && q.ScheduledStart is { } s)
        {
            return $"Opens {Local(s)}";
        }

        if (q.Deadline is not { } d)
        {
            return "No expiry";
        }

        var span = d - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero)
        {
            return "Overdue";
        }

        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d left";
        }

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h left" : $"{(int)span.TotalMinutes}m left";
    }

    public string Local(DateTimeOffset? utc)
    {
        if (utc is not { } t)
        {
            return "—";
        }

        if (!TimeZoneService.IsValidZone(ZoneId))
        {
            return t.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);
        return TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd HH:mm");
    }
}
