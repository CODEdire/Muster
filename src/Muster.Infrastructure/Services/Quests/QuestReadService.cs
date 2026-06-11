using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>A participant on a board quest, as the UI needs it (no entity / tracking leaks).</summary>
public record QuestParticipantView(ulong UserId, QuestParticipantStatus Status, string? Note, string? ReviewNote, ulong? ReviewedBy);

/// <summary>A quest row for the board, projected from the aggregate.</summary>
public record QuestBoardItem(
    Guid Id, string Name, string Description, QuestOrigin Origin,
    long RewardAmount, Guid RewardCurrencyId, QuestTier Tier, long BonusPoints, int Capacity,
    QuestStatus Status, DateTimeOffset? ScheduledStart, DateTimeOffset? Deadline, ulong OwnerId,
    IReadOnlyList<QuestParticipantView> Participants, Guid? QuestTypeId = null);

/// <summary>A paged board view plus the lookups the page renders with.</summary>
public record QuestBoardPage(
    IReadOnlyList<QuestBoardItem> Items, int Total, int Page, int TotalPages,
    IReadOnlyDictionary<Guid, string> Codes, IReadOnlyDictionary<ulong, string> Names,
    IReadOnlyDictionary<ulong, string> Avatars, Muster.Domain.Entities.Guilds.GuildQuestSettings Settings);

/// <summary>The fields the edit form prefills from.</summary>
public record QuestEditView(QuestOrigin Origin, string Name, string Description, long RewardAmount, QuestTier Tier, int Capacity, DateTimeOffset? Deadline, Guid? QuestTypeId);

/// <summary>A participant as the detail page shows them (name/avatar + reviewer resolved).</summary>
public record QuestDetailParticipant(ulong UserId, string Name, string? AvatarUrl, QuestParticipantStatus Status, string? Note, string? ReviewNote, ulong? ReviewedBy, string? ReviewedByName, DateTimeOffset? SubmittedAt);

/// <summary>The full quest for the detail page — everything the card omits, with names/avatars resolved.</summary>
public record QuestDetailView(
    Guid Id, string Name, string Description, QuestOrigin Origin, QuestStatus Status,
    long RewardAmount, string RewardCode, QuestTier Tier, long BonusPoints, int Capacity,
    ulong OwnerId, string OwnerName, string? OwnerAvatar, ulong? DisputedBy, string? DisputedByName, string? DisputeReason,
    DateTimeOffset CreatedAt, DateTimeOffset? ScheduledStart, DateTimeOffset? Deadline,
    Guid? QuestTypeId, string? TypeName, string? TypeIcon,
    IReadOnlyList<QuestDetailParticipant> Participants);

/// <summary>
/// Viewer-aware filter for <see cref="QuestDetailView"/>. The underlying read returns the unscrubbed (staff/system)
/// shape; this helper masks private fields so a random guild member can't read another worker's submission text or
/// a manager's rejection reasoning when fetching the quest via slash command / web detail page / public API.
/// </summary>
/// <remarks>
/// <para>Privileged viewers (manager + owner) see everything. The disputing party also sees their own dispute
/// reason. Per-participant notes/review notes/reviewer identity follow the same rule on a per-row basis: the
/// worker sees their own; quest staff see all; everyone else sees none.</para>
/// <para>Internal callers (DM push, board renderers, reminders, audit middleware) should NOT scrub — they need
/// the full view to render staff cards or deliver notifications to the actual subject.</para>
/// </remarks>
public static class QuestDetailViewScrub
{
    /// <summary>Return a copy of <paramref name="detail"/> with private fields nulled for an unprivileged viewer.</summary>
    public static QuestDetailView ForViewer(QuestDetailView detail, ulong viewerUserId, bool isManager)
    {
        var isQuestStaff = isManager || viewerUserId == detail.OwnerId;
        var seeDispute = isQuestStaff || (detail.DisputedBy is { } d && d == viewerUserId);

        var participants = detail.Participants
            .Select(p => (isQuestStaff || p.UserId == viewerUserId)
                ? p
                : p with { Note = null, ReviewNote = null, ReviewedBy = null, ReviewedByName = null })
            .ToList();

        return detail with
        {
            DisputedBy = seeDispute ? detail.DisputedBy : null,
            DisputedByName = seeDispute ? detail.DisputedByName : null,
            DisputeReason = seeDispute ? detail.DisputeReason : null,
            Participants = participants,
        };
    }
}

/// <summary>A compact open-board line for the bot's text list.</summary>
public record QuestListItem(string Name, QuestOrigin Origin, long RewardAmount, string Code, string State, DateTimeOffset? Deadline);

/// <summary>
/// Read side of the quest board (CQRS-lite: commands go through the bus, reads through this interface).
/// Returns DTOs so adapters never touch <see cref="MusterDbContext"/> or tracked entities.
/// </summary>
public interface IQuestReadService
{
    Task<QuestBoardPage> GetBoardAsync(ulong guildId, ulong userId, bool isManager, string tab, string? type,
        string? search, string sort, bool desc, int page, int pageSize,
        Guid? questTypeId = null, QuestTier? tier = null, CancellationToken ct = default);

    Task<QuestEditView?> GetForEditAsync(ulong guildId, Guid questId, CancellationToken ct = default);

    /// <summary>Full quest for the detail page (description, issuer, participants), or null if not found.</summary>
    Task<QuestDetailView?> GetQuestDetailAsync(ulong guildId, Guid questId, CancellationToken ct = default);

    Task<IReadOnlyList<QuestListItem>> GetOpenBoardAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>A guild's quest settings (for the post/edit forms' tier + final-approval options).</summary>
    Task<Muster.Domain.Entities.Guilds.GuildQuestSettings> GetSettingsAsync(ulong guildId, CancellationToken ct = default);

    /// <summary>A guild's quest types (admin vocab) ordered for pickers/filters — for the post/edit forms + the board.</summary>
    Task<IReadOnlyList<Muster.Domain.Entities.Quests.QuestType>> GetQuestTypesAsync(ulong guildId, CancellationToken ct = default);
}

public sealed class QuestReadService(MusterDbContext db) : IQuestReadService
{
    public async Task<QuestBoardPage> GetBoardAsync(ulong guildId, ulong userId, bool isManager, string tab, string? type,
        string? search, string sort, bool desc, int page, int pageSize,
        Guid? questTypeId = null, QuestTier? tier = null, CancellationToken ct = default)
    {
        var settings = await db.GetQuestSettingsAsync(guildId, ct);
        var codes = await db.Currencies.Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        var query = db.Quests.AsNoTracking().Include(m => m.Participants).Where(m => m.GuildId == guildId);

        query = tab switch
        {
            "history" => isManager
                ? query.Where(m => m.Status == QuestStatus.Closed || m.Status == QuestStatus.Cancelled || m.Status == QuestStatus.Expired)
                : query.Where(m => (m.Status == QuestStatus.Closed || m.Status == QuestStatus.Cancelled || m.Status == QuestStatus.Expired)
                    && (m.OwnerId == userId || m.Participants.Any(p => p.UserId == userId))),

            // Action Needed = the quest-manager review queue: guild submissions awaiting approval + player quests at
            // intake / final sign-off / arbitration. Empty for non-managers (their actions come via DM/the card).
            "actionneeded" => isManager
                ? query.Where(m =>
                    (m.Origin == QuestOrigin.Guild && m.Status == QuestStatus.Open && m.Participants.Any(p => p.Status == QuestParticipantStatus.Submitted))
                    || (m.Origin == QuestOrigin.Player && (m.Status == QuestStatus.PendingApproval || m.Status == QuestStatus.PendingFinal || m.Status == QuestStatus.Disputed)))
                : query.Where(_ => false),

            // Active = everything in flight (non-terminal).
            _ => query.Where(m => m.Status != QuestStatus.Closed && m.Status != QuestStatus.Cancelled && m.Status != QuestStatus.Expired),
        };

        // Scope: guild (official duty — shown only when claimed) / player (any author) / mine (anything personal:
        // a player quest I posted, or any quest — incl. guild — I've claimed/participated in).
        if (type == "guild")
        {
            query = query.Where(m => m.Origin == QuestOrigin.Guild);
        }
        else if (type == "player")
        {
            query = query.Where(m => m.Origin == QuestOrigin.Player);
        }
        else if (type == "mine")
        {
            query = query.Where(m =>
                (m.Origin == QuestOrigin.Player && m.OwnerId == userId)   // bounties I posted
                || m.Participants.Any(p => p.UserId == userId));          // anything I claimed (incl. guild duties)
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(m => m.Name.Contains(s) || m.Description.Contains(s));
        }

        if (questTypeId is { } qt)
        {
            query = query.Where(m => m.QuestTypeId == qt);
        }

        if (tier is { } ti)
        {
            query = query.Where(m => m.Tier == ti);
        }

        var total = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)Math.Max(1, pageSize)));
        var clamped = Math.Clamp(page <= 0 ? 1 : page, 1, totalPages);

        var quests = await Sorted(query, sort, desc).Skip((clamped - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // Resolve names/avatars for everyone shown: participants, owners (bounty posters), and reviewers.
        var ids = quests.SelectMany(m => m.Participants.Select(p => p.UserId))
            .Concat(quests.Select(m => m.OwnerId))
            .Concat(quests.SelectMany(m => m.Participants.Where(p => p.ReviewedBy is not null).Select(p => p.ReviewedBy!.Value)))
            .Distinct().ToList();
        var users = await db.Users.Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.GlobalName ?? u.Username, u.AvatarHash }).ToListAsync(ct);
        var names = users.ToDictionary(u => u.Id, u => u.Name);
        var avatars = users.Where(u => u.AvatarHash != null).ToDictionary(u => u.Id, u => Discord.DiscordCdn.AvatarUrl(u.Id, u.AvatarHash)!);

        var items = quests.Select(m => new QuestBoardItem(
            m.Id, m.Name, m.Description, m.Origin, m.RewardAmount, m.RewardCurrencyId, m.Tier, m.BonusPoints, m.Capacity,
            m.Status, m.ScheduledStart, m.Deadline, m.OwnerId,
            m.Participants.Select(p => new QuestParticipantView(p.UserId, p.Status, p.Note, p.ReviewNote, p.ReviewedBy)).ToList(),
            m.QuestTypeId)).ToList();

        return new QuestBoardPage(items, total, clamped, totalPages, codes, names, avatars, settings);
    }

    public async Task<QuestEditView?> GetForEditAsync(ulong guildId, Guid questId, CancellationToken ct = default)
    {
        var q = await db.Quests.AsNoTracking().FirstOrDefaultAsync(m => m.Id == questId && m.GuildId == guildId, ct);
        return q is null ? null : new QuestEditView(q.Origin, q.Name, q.Description, q.RewardAmount, q.Tier, q.Capacity, q.Deadline, q.QuestTypeId);
    }

    public async Task<QuestDetailView?> GetQuestDetailAsync(ulong guildId, Guid questId, CancellationToken ct = default)
    {
        var q = await db.Quests.AsNoTracking().Include(m => m.Participants)
            .FirstOrDefaultAsync(m => m.Id == questId && m.GuildId == guildId, ct);
        if (q is null)
        {
            return null;
        }

        var code = await db.Currencies.Where(c => c.Id == q.RewardCurrencyId).Select(c => c.Code).FirstOrDefaultAsync(ct) ?? "?";

        // Resolve the quest type (name + icon) for the detail hero — the type is admin vocab, not on the quest row.
        var type = q.QuestTypeId is { } tid
            ? await db.QuestTypes.AsNoTracking().Where(t => t.Id == tid).Select(t => new { t.Name, t.Icon }).FirstOrDefaultAsync(ct)
            : null;

        var ids = q.Participants.Select(p => p.UserId)
            .Concat(q.Participants.Where(p => p.ReviewedBy is not null).Select(p => p.ReviewedBy!.Value))
            .Append(q.OwnerId)
            .Concat(q.DisputedBy is { } db2 ? [db2] : Array.Empty<ulong>())
            .Distinct().ToList();
        var users = await db.Users.Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.GlobalName ?? u.Username, u.AvatarHash }).ToListAsync(ct);
        var names = users.ToDictionary(u => u.Id, u => u.Name);
        var avatars = users.ToDictionary(u => u.Id, u => Discord.DiscordCdn.AvatarUrl(u.Id, u.AvatarHash));

        string NameOf(ulong id) => names.GetValueOrDefault(id, $"member {id}");
        string? AvatarOf(ulong id) => avatars.GetValueOrDefault(id);

        var participants = q.Participants
            .OrderByDescending(p => p.SubmittedAt ?? p.ClaimedAt)
            .Select(p => new QuestDetailParticipant(p.UserId, NameOf(p.UserId), AvatarOf(p.UserId), p.Status, p.Note, p.ReviewNote,
                p.ReviewedBy, p.ReviewedBy is { } rb ? NameOf(rb) : null, p.SubmittedAt))
            .ToList();

        return new QuestDetailView(
            q.Id, q.Name, q.Description, q.Origin, q.Status, q.RewardAmount, code, q.Tier, q.BonusPoints, q.Capacity,
            q.OwnerId, NameOf(q.OwnerId), AvatarOf(q.OwnerId), q.DisputedBy, q.DisputedBy is { } d ? NameOf(d) : null, q.DisputeReason,
            q.CreatedAt, q.ScheduledStart, q.Deadline,
            q.QuestTypeId, type?.Name, type?.Icon,
            participants);
    }

    public Task<Muster.Domain.Entities.Guilds.GuildQuestSettings> GetSettingsAsync(ulong guildId, CancellationToken ct = default) => db.GetQuestSettingsAsync(guildId, ct);

    public async Task<IReadOnlyList<Muster.Domain.Entities.Quests.QuestType>> GetQuestTypesAsync(ulong guildId, CancellationToken ct = default)
        => await db.QuestTypes.AsNoTracking().Where(t => t.GuildId == guildId)
            .OrderBy(t => t.Sort).ThenBy(t => t.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<QuestListItem>> GetOpenBoardAsync(ulong guildId, CancellationToken ct = default)
    {
        var board = await db.Quests.AsNoTracking().Include(m => m.Participants)
            .Where(m => m.GuildId == guildId
                && (m.Status == QuestStatus.Open || m.Status == QuestStatus.Scheduled || m.Status == QuestStatus.Disputed
                    || m.Status == QuestStatus.PendingApproval || m.Status == QuestStatus.PendingFinal))
            .OrderBy(m => m.ScheduledStart ?? m.CreatedAt)
            .ToListAsync(ct);

        var codes = await db.Currencies.Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        return board.Select(m => new QuestListItem(
            m.Name, m.Origin, m.RewardAmount, codes.GetValueOrDefault(m.RewardCurrencyId, "?"), State(m), m.Deadline)).ToList();
    }

    private static string State(GuildQuest m) => m.Status switch
    {
        QuestStatus.PendingApproval => "pending approval",
        QuestStatus.PendingFinal => "awaiting final sign-off",
        QuestStatus.Scheduled => "scheduled",
        QuestStatus.Disputed => "disputed",
        _ when m.Participants.Any(p => p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted) => "taken",
        _ => "open",
    };

    private static IQueryable<GuildQuest> Sorted(IQueryable<GuildQuest> q, string sort, bool desc) => sort switch
    {
        "name" => desc ? q.OrderByDescending(m => m.Name) : q.OrderBy(m => m.Name),
        "type" => desc ? q.OrderByDescending(m => m.Origin) : q.OrderBy(m => m.Origin),
        "reward" => desc ? q.OrderByDescending(m => m.RewardAmount) : q.OrderBy(m => m.RewardAmount),
        "status" => desc ? q.OrderByDescending(m => m.Status) : q.OrderBy(m => m.Status),
        "opens" => desc ? q.OrderByDescending(m => m.ScheduledStart) : q.OrderBy(m => m.ScheduledStart),
        "closes" => desc ? q.OrderByDescending(m => m.Deadline) : q.OrderBy(m => m.Deadline),
        _ => desc ? q.OrderByDescending(m => m.CreatedAt) : q.OrderBy(m => m.CreatedAt),
    };
}
