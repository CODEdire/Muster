using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Persistence.Queries;

/// <summary>
/// Queries over the GuildQuest aggregate (GuildQuest + its QuestParticipants). Write paths load the whole
/// aggregate (<see cref="FindQuestAsync"/>) so the consistency boundary travels together and transitions
/// operate on the in-memory participant collection; reads use lean projections/scalars and only Include
/// participants where the result actually needs them (board "taken" state, expiry's submitted check).
/// </summary>
public static class QuestQueries
{
    // --- Aggregate loads (write paths) ---

    /// <summary>Load a quest with its participants — the full aggregate.</summary>
    public static Task<GuildQuest?> FindQuestAsync(this MusterDbContext db, Guid id, CancellationToken ct = default)
        => db.Quests.Include(m => m.Participants).FirstOrDefaultAsync(m => m.Id == id, ct);

    /// <summary>Load a quest (with participants) scoped to a guild.</summary>
    public static Task<GuildQuest?> FindQuestInGuildAsync(this MusterDbContext db, ulong guildId, Guid id, CancellationToken ct = default)
        => db.Quests.Include(m => m.Participants).FirstOrDefaultAsync(m => m.Id == id && m.GuildId == guildId, ct);

    // --- Lean scalars ---

    /// <summary>The quest's origin, for routing — no entity materialization.</summary>
    public static async Task<QuestOrigin?> FindQuestOriginAsync(this MusterDbContext db, Guid id, CancellationToken ct = default)
        => await db.Quests.Where(m => m.Id == id).Select(m => (QuestOrigin?)m.Origin).FirstOrDefaultAsync(ct);

    /// <summary>The quest's current status — a scalar read (e.g. to confirm a post-action transition).</summary>
    public static async Task<QuestStatus?> FindQuestStatusAsync(this MusterDbContext db, Guid id, CancellationToken ct = default)
        => await db.Quests.Where(m => m.Id == id).Select(m => (QuestStatus?)m.Status).FirstOrDefaultAsync(ct);

    /// <summary>How many quests the poster currently has in a non-terminal state.</summary>
    public static Task<int> CountActiveQuestsByPosterAsync(this MusterDbContext db, ulong guildId, ulong ownerId, CancellationToken ct = default)
        => db.Quests.CountAsync(m => m.GuildId == guildId && m.OwnerId == ownerId
            && (m.Status == QuestStatus.Open || m.Status == QuestStatus.Scheduled || m.Status == QuestStatus.PendingApproval
                || m.Status == QuestStatus.PendingFinal || m.Status == QuestStatus.Disputed), ct);

    /// <summary>How many quests the user is actively working on (claimed / submitted / in revision).</summary>
    public static Task<int> CountActiveClaimsAsync(this MusterDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
        => db.QuestParticipants.CountAsync(p => p.Quest!.GuildId == guildId && p.UserId == userId
            && (p.Status == QuestParticipantStatus.Claimed || p.Status == QuestParticipantStatus.Submitted
                || p.Status == QuestParticipantStatus.RevisionRequested), ct);

    // --- Read lists ---

    /// <summary>The unified quest board: every live quest of either origin, oldest-scheduled first.</summary>
    public static Task<List<GuildQuest>> ListQuestBoardAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Quests
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId
                && (m.Status == QuestStatus.Open || m.Status == QuestStatus.Scheduled || m.Status == QuestStatus.Disputed
                    || m.Status == QuestStatus.PendingApproval || m.Status == QuestStatus.PendingFinal))
            .OrderBy(m => m.ScheduledStart ?? m.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Open or scheduled guild quests.</summary>
    public static Task<List<GuildQuest>> ListOpenGuildQuestsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Quests
            .Where(m => m.GuildId == guildId && m.Origin == QuestOrigin.Guild
                && (m.Status == QuestStatus.Open || m.Status == QuestStatus.Scheduled))
            .OrderBy(m => m.ScheduledStart ?? m.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Live player bounties (with participants, for taken-state).</summary>
    public static Task<List<GuildQuest>> ListOpenPersonalQuestsAsync(this MusterDbContext db, ulong guildId, CancellationToken ct = default)
        => db.Quests
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Origin == QuestOrigin.Player
                && (m.Status == QuestStatus.Open || m.Status == QuestStatus.Scheduled
                    || m.Status == QuestStatus.PendingApproval || m.Status == QuestStatus.PendingFinal))
            .OrderBy(m => m.ScheduledStart ?? m.CreatedAt)
            .ToListAsync(ct);

    // --- Sweeps ---

    /// <summary>Scheduled quests whose start time has arrived (optionally scoped to one guild).</summary>
    public static Task<List<GuildQuest>> ListScheduledDueAsync(this MusterDbContext db, DateTimeOffset now, ulong? guildId = null, CancellationToken ct = default)
        => db.Quests
            .Where(m => (guildId == null || m.GuildId == guildId)
                && m.Status == QuestStatus.Scheduled && m.ScheduledStart != null && m.ScheduledStart <= now)
            .ToListAsync(ct);

    /// <summary>Past-deadline guild quests (with participants, to skip ones awaiting approval).</summary>
    public static Task<List<GuildQuest>> ListExpiredGuildQuestsAsync(this MusterDbContext db, DateTimeOffset now, CancellationToken ct = default)
        => db.Quests
            .Include(m => m.Participants)
            .Where(m => m.Origin == QuestOrigin.Guild
                && (m.Status == QuestStatus.Open || m.Status == QuestStatus.Scheduled)
                && m.Deadline != null && m.Deadline < now)
            .ToListAsync(ct);

    /// <summary>Past-deadline player bounties (with participants, for refund + submitted check; optional guild scope).</summary>
    public static Task<List<GuildQuest>> ListExpiredPersonalQuestsAsync(this MusterDbContext db, DateTimeOffset now, ulong? guildId = null, CancellationToken ct = default)
        => db.Quests
            .Include(m => m.Participants)
            .Where(m => (guildId == null || m.GuildId == guildId) && m.Origin == QuestOrigin.Player
                && (m.Status == QuestStatus.Open || m.Status == QuestStatus.Scheduled || m.Status == QuestStatus.PendingApproval)
                && m.Deadline != null && m.Deadline < now)
            .ToListAsync(ct);

    // --- Maintenance (anti-staleness) ---

    /// <summary>Player bounties stuck awaiting intake past the cutoff.</summary>
    public static Task<List<(Guid Id, string Name)>> ListStaleIntakeAsync(this MusterDbContext db, ulong guildId, DateTimeOffset cutoff, CancellationToken ct = default)
        => db.Quests
            .Where(m => m.GuildId == guildId && m.Origin == QuestOrigin.Player
                && m.Status == QuestStatus.PendingApproval && m.StatusChangedAt < cutoff)
            .Select(m => new ValueTuple<Guid, string>(m.Id, m.Name))
            .ToListAsync(ct);

    /// <summary>Player bounties stuck awaiting final sign-off past the cutoff.</summary>
    public static Task<List<(Guid Id, string Name)>> ListStaleFinalAsync(this MusterDbContext db, ulong guildId, DateTimeOffset cutoff, CancellationToken ct = default)
        => db.Quests
            .Where(m => m.GuildId == guildId && m.Origin == QuestOrigin.Player
                && m.Status == QuestStatus.PendingFinal && m.StatusChangedAt < cutoff)
            .Select(m => new ValueTuple<Guid, string>(m.Id, m.Name))
            .ToListAsync(ct);

    /// <summary>Open quests with a claim that has sat idle (no submission) past the cutoff (with participants).</summary>
    public static Task<List<GuildQuest>> ListStaleClaimsAsync(this MusterDbContext db, ulong guildId, DateTimeOffset cutoff, CancellationToken ct = default)
        => db.Quests
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Status == QuestStatus.Open
                && m.Participants.Any(p => p.Status == QuestParticipantStatus.Claimed && p.ClaimedAt != null && p.ClaimedAt < cutoff))
            .ToListAsync(ct);

    /// <summary>Open quests with a submission left unreviewed past the cutoff (with participants).</summary>
    public static Task<List<GuildQuest>> ListStaleSubmissionsAsync(this MusterDbContext db, ulong guildId, DateTimeOffset cutoff, CancellationToken ct = default)
        => db.Quests
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Status == QuestStatus.Open
                && m.Participants.Any(p => p.Status == QuestParticipantStatus.Submitted && p.SubmittedAt != null && p.SubmittedAt < cutoff))
            .ToListAsync(ct);

    /// <summary>Disputed bounties left unresolved past the cutoff (with participants, to find the taker + disputant).</summary>
    public static Task<List<GuildQuest>> ListStaleDisputedAsync(this MusterDbContext db, ulong guildId, DateTimeOffset cutoff, CancellationToken ct = default)
        => db.Quests
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Status == QuestStatus.Disputed && m.StatusChangedAt < cutoff)
            .ToListAsync(ct);

    // --- Reminders ---

    /// <summary>Open quests whose deadline falls inside the reminder window (now &lt; deadline ≤ <paramref name="windowEnd"/>)
    /// and that haven't been reminded yet — with participants, to target active workers / a taker-less owner.</summary>
    public static Task<List<GuildQuest>> ListQuestsDueForDeadlineReminderAsync(
        this MusterDbContext db, ulong guildId, DateTimeOffset now, DateTimeOffset windowEnd, CancellationToken ct = default)
        => db.Quests
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Status == QuestStatus.Open
                && m.Deadline != null && m.Deadline > now && m.Deadline <= windowEnd
                && m.DeadlineReminderSentAt == null)
            .ToListAsync(ct);
}
