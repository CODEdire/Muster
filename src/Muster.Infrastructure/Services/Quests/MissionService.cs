using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Ledger;
using Muster.Infrastructure.Services.Membership;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>
/// The missions board. Supports both quest tasks (claim → submit → approve, rewarded on approval) and
/// event ops (sign-up → attendance). Awards go through <see cref="AwardService"/> and are idempotent
/// per (mission, user), so approving twice never double-rewards.
/// </summary>
public class MissionService(
    MusterDbContext db,
    AwardService awards,
    GuildAuthorizationService auth,
    IQuestNotifier notifier,
    IQuestRewardSink rewards)
{
    private static void SetStatus(Mission mission, MissionStatus status)
    {
        mission.Status = status;
        mission.StatusChangedAt = DateTimeOffset.UtcNow;
    }

    private Task NotifyAsync(Mission m, QuestLifecycleEvent ev, ulong? target, string detail, CancellationToken ct)
        => notifier.NotifyAsync(new QuestNotification(m.GuildId, m.Id, m.Name, ev, target, detail), ct);

    private Task RewardAsync(Mission m, ulong completer, CancellationToken ct)
        => rewards.OnCompletedAsync(new QuestCompletion(
            m.GuildId, m.Id, m.Name, completer, m.Origin, m.RewardCurrencyId, m.RewardAmount, m.BonusPoints, m.Tier), ct);

    public async Task<Mission> CreateQuestAsync(
        ulong guildId, string name, string description, ulong createdBy,
        Guid rewardCurrencyId, long rewardAmount, DateTimeOffset? deadline = null, DateTimeOffset? startsAt = null,
        long bonusPoints = 0, QuestTier tier = QuestTier.None, bool repeatable = false, int capacity = 1,
        bool requiresApproval = true, CancellationToken ct = default)
    {
        var scheduled = startsAt is { } s && s > DateTimeOffset.UtcNow;
        var mission = new Mission
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Type = MissionType.Quest,
            Name = name,
            Description = description,
            Status = scheduled ? MissionStatus.Scheduled : MissionStatus.Open,
            StatusChangedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
            OwnerId = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
            RewardCurrencyId = rewardCurrencyId,
            RewardAmount = rewardAmount,
            BonusPoints = bonusPoints,
            Tier = tier,
            Capacity = Math.Max(1, capacity),
            ScheduledStart = startsAt,
            Deadline = deadline,
            IsRepeatable = repeatable,
            RequiresApproval = requiresApproval,
        };
        db.Missions.Add(mission);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.Created, null, "Quest created.", ct);
        return mission;
    }

    /// <summary>Create a guild quest minting the given currency (by code). Returns null if the currency is unknown.</summary>
    public async Task<Mission?> CreateGuildQuestAsync(
        ulong guildId, string name, string description, ulong createdBy, string currencyCode, long rewardAmount,
        DateTimeOffset? deadline = null, DateTimeOffset? startsAt = null, long bonusPoints = 0, CancellationToken ct = default)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.GuildId == guildId && c.Code == code, ct);
        if (currency is null)
        {
            return null;
        }

        return await CreateQuestAsync(guildId, name, description, createdBy, currency.Id, rewardAmount, deadline, startsAt, bonusPoints, ct: ct);
    }

    /// <summary>Create a quest that rewards the guild's POINTS currency.</summary>
    public async Task<Mission> CreateQuestPointsAsync(
        ulong guildId, string name, string description, ulong createdBy, long rewardPoints,
        DateTimeOffset? deadline = null, bool repeatable = false, CancellationToken ct = default)
    {
        var points = await db.Currencies.FirstOrDefaultAsync(
            c => c.GuildId == guildId && c.Code == GuildProvisioningService.PointsCurrencyCode, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

        return await CreateQuestAsync(guildId, name, description, createdBy, points.Id, rewardPoints, deadline, repeatable: repeatable, ct: ct);
    }

    /// <summary>Open or scheduled guild quests (player-funded bounties are listed separately).</summary>
    public async Task<IReadOnlyList<Mission>> ListOpenQuestsAsync(ulong guildId, CancellationToken ct = default)
        => await db.Missions
            .Where(m => m.GuildId == guildId && m.Type == MissionType.Quest && m.Origin == MissionOrigin.Guild
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Scheduled))
            .OrderBy(m => m.ScheduledStart ?? m.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Flip scheduled quests (both origins) in one guild whose start time has arrived to Open.</summary>
    public Task<int> ActivateScheduledAsync(ulong guildId, DateTimeOffset now, CancellationToken ct = default)
        => ActivateAsync(m => m.GuildId == guildId, now, ct);

    /// <summary>Flip scheduled quests across all guilds whose start time has arrived to Open. For the scheduled sweep.</summary>
    public Task<int> ActivateScheduledAsync(DateTimeOffset now, CancellationToken ct = default)
        => ActivateAsync(_ => true, now, ct);

    private async Task<int> ActivateAsync(
        System.Linq.Expressions.Expression<Func<Mission, bool>> scope, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.Missions
            .Where(scope)
            .Where(m => m.Type == MissionType.Quest
                && m.Status == MissionStatus.Scheduled && m.ScheduledStart != null && m.ScheduledStart <= now)
            .ToListAsync(ct);

        foreach (var mission in due)
        {
            SetStatus(mission, MissionStatus.Open);
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    /// <summary>
    /// Expire past-deadline guild quests across all guilds. Guild quests mint their reward on approval,
    /// so there's nothing to refund — expiring just closes the unfinished quest. Quests with a submission
    /// awaiting approval are left alone. Player-funded quests are expired (and refunded) by BountyService.
    /// </summary>
    public async Task<int> ExpireDueQuestsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var due = await db.Missions
            .Include(m => m.Participants)
            .Where(m => m.Type == MissionType.Quest && m.Origin == MissionOrigin.Guild
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Scheduled)
                && m.Deadline != null && m.Deadline < now)
            .ToListAsync(ct);

        var expired = due.Where(m => m.Participants.All(p => p.Status != MissionParticipantStatus.Submitted)).ToList();
        foreach (var mission in expired)
        {
            SetStatus(mission, MissionStatus.Expired);
        }

        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    /// <summary>Cancel a guild quest that hasn't been completed (no escrow to refund — the reward is minted on approval).</summary>
    public async Task CancelQuestAsync(Guid missionId, ulong actorId, CancellationToken ct = default)
    {
        var mission = await db.Missions.FirstOrDefaultAsync(m => m.Id == missionId, ct)
            ?? throw new InvalidOperationException("Quest not found.");

        if (mission.Status is not (MissionStatus.Open or MissionStatus.Scheduled))
        {
            throw new InvalidOperationException("This quest can't be cancelled in its current state.");
        }

        SetStatus(mission, MissionStatus.Cancelled);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MissionParticipant> ClaimAsync(Guid missionId, ulong userId, CancellationToken ct = default)
    {
        var mission = await db.Missions.FirstOrDefaultAsync(m => m.Id == missionId, ct)
            ?? throw new InvalidOperationException("Quest not found.");

        if (mission.Status == MissionStatus.Scheduled)
        {
            throw new InvalidOperationException("This quest hasn't started yet.");
        }

        if (mission.Status != MissionStatus.Open)
        {
            throw new InvalidOperationException("This quest isn't open.");
        }

        if (!await auth.IsParticipantAsync(mission.GuildId, userId, ct))
        {
            throw new InvalidOperationException("You're not eligible to participate in this server.");
        }

        var existing = await db.MissionParticipants
            .FirstOrDefaultAsync(p => p.MissionId == missionId && p.UserId == userId, ct);
        if (existing is not null)
        {
            return existing;
        }

        // Capacity caps how many members may work on / complete a guild quest at once.
        var taken = await db.MissionParticipants.CountAsync(p => p.MissionId == missionId
            && (p.Status == MissionParticipantStatus.Claimed || p.Status == MissionParticipantStatus.Submitted
                || p.Status == MissionParticipantStatus.RevisionRequested || p.Status == MissionParticipantStatus.Approved), ct);
        if (taken >= Math.Max(1, mission.Capacity))
        {
            throw new InvalidOperationException("This quest is full — all slots are taken.");
        }

        var participant = new MissionParticipant
        {
            Id = Guid.NewGuid(),
            MissionId = missionId,
            UserId = userId,
            Status = MissionParticipantStatus.Claimed,
            ClaimedAt = DateTimeOffset.UtcNow,
        };
        db.MissionParticipants.Add(participant);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.Claimed, mission.OwnerId, $"Claimed by <@{userId}>.", ct);
        return participant;
    }

    public async Task SubmitAsync(Guid missionId, ulong userId, string? note = null, CancellationToken ct = default)
    {
        var participant = await GetParticipantAsync(missionId, userId, ct);
        if (participant.Status is not (MissionParticipantStatus.Claimed or MissionParticipantStatus.RevisionRequested))
        {
            throw new InvalidOperationException("This quest isn't claimed by you, or has already been submitted.");
        }

        participant.Status = MissionParticipantStatus.Submitted;
        participant.SubmittedAt = DateTimeOffset.UtcNow;
        participant.Note = note;
        await db.SaveChangesAsync(ct);

        var mission = await db.Missions.FirstAsync(m => m.Id == missionId, ct);
        await NotifyAsync(mission, QuestLifecycleEvent.Submitted, null, $"<@{userId}> submitted for review.", ct);
    }

    /// <summary>Approve a quest submission and award the reward. Idempotent per (mission, user).</summary>
    public async Task ApproveAsync(Guid missionId, ulong userId, ulong reviewerId, CancellationToken ct = default)
    {
        var mission = await db.Missions.FirstOrDefaultAsync(m => m.Id == missionId, ct)
            ?? throw new InvalidOperationException($"Mission {missionId} not found.");
        var participant = await GetParticipantAsync(missionId, userId, ct);

        participant.Status = MissionParticipantStatus.Approved;
        participant.ReviewedBy = reviewerId;
        participant.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // A guild quest closes once its capacity of completions is reached, unless it's repeatable
        // (repeatable quests stay open for unlimited completions).
        var approvedCount = await db.MissionParticipants.CountAsync(
            p => p.MissionId == missionId && p.Status == MissionParticipantStatus.Approved, ct);
        if (!mission.IsRepeatable && approvedCount >= Math.Max(1, mission.Capacity))
        {
            SetStatus(mission, MissionStatus.Closed);
            await db.SaveChangesAsync(ct);
        }

        await awards.AwardAsync(
            mission.GuildId, userId, mission.RewardCurrencyId, mission.RewardAmount,
            LedgerSourceType.Mission, $"mission:{missionId}:user:{userId}",
            $"Quest approved: {mission.Name}", ct);

        if (mission.BonusPoints > 0)
        {
            await awards.AwardPointsAsync(
                mission.GuildId, userId, mission.BonusPoints,
                LedgerSourceType.Mission, $"mission:{missionId}:bonus:{userId}",
                $"Quest bonus: {mission.Name}", ct);
        }

        await RewardAsync(mission, userId, ct);
        await NotifyAsync(mission, QuestLifecycleEvent.Settled, userId, $"Approved — reward granted to <@{userId}>.", ct);
    }

    /// <summary>Final reject of a guild-quest submission (no reward). The quest stays open for another taker.</summary>
    public async Task RejectAsync(Guid missionId, ulong userId, ulong reviewerId, string? note = null, CancellationToken ct = default)
    {
        var participant = await GetParticipantAsync(missionId, userId, ct);
        participant.Status = MissionParticipantStatus.Rejected;
        participant.ReviewedBy = reviewerId;
        participant.ReviewedAt = DateTimeOffset.UtcNow;
        participant.ReviewNote = note;
        await db.SaveChangesAsync(ct);

        var mission = await db.Missions.FirstAsync(m => m.Id == missionId, ct);
        await NotifyAsync(mission, QuestLifecycleEvent.RejectedAtIntake, userId, "Submission rejected.", ct);
    }

    /// <summary>Send a guild-quest submission back to the worker to revise and resubmit (capped by guild config).</summary>
    public async Task RequestRevisionAsync(Guid missionId, ulong userId, ulong reviewerId, string? note = null, CancellationToken ct = default)
    {
        var mission = await db.Missions.FirstOrDefaultAsync(m => m.Id == missionId, ct)
            ?? throw new InvalidOperationException($"Mission {missionId} not found.");
        var participant = await GetParticipantAsync(missionId, userId, ct);
        if (participant.Status != MissionParticipantStatus.Submitted)
        {
            throw new InvalidOperationException("Only a submitted quest can be sent back for revision.");
        }

        var max = (await db.Guilds.FirstOrDefaultAsync(g => g.Id == mission.GuildId, ct))?.Settings.MaxRevisions ?? 0;
        if (max > 0 && participant.RevisionCount >= max)
        {
            throw new InvalidOperationException($"This submission has already used its {max} revision(s) — approve or reject it instead.");
        }

        participant.Status = MissionParticipantStatus.RevisionRequested;
        participant.ReviewedBy = reviewerId;
        participant.ReviewedAt = DateTimeOffset.UtcNow;
        participant.ReviewNote = note;
        participant.RevisionCount++;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.RevisionRequested, userId, note ?? "Please revise and resubmit.", ct);
    }

    // --- Event ops (scheduled missions with sign-up + attendance) ---

    public async Task<Mission> CreateEventOpPointsAsync(
        ulong guildId, string name, string description, ulong createdBy, long rewardPoints,
        DateTimeOffset? scheduledStart = null, DateTimeOffset? scheduledEnd = null, CancellationToken ct = default)
    {
        var points = await db.Currencies.FirstOrDefaultAsync(
            c => c.GuildId == guildId && c.Code == GuildProvisioningService.PointsCurrencyCode, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

        var mission = new Mission
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Type = MissionType.EventOp,
            Name = name,
            Description = description,
            Status = MissionStatus.Open,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
            RewardCurrencyId = points.Id,
            RewardAmount = rewardPoints,
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledEnd,
        };
        db.Missions.Add(mission);
        await db.SaveChangesAsync(ct);
        return mission;
    }

    public async Task<IReadOnlyList<Mission>> ListOpenEventOpsAsync(ulong guildId, CancellationToken ct = default)
        => await db.Missions
            .Where(m => m.GuildId == guildId && m.Type == MissionType.EventOp && m.Status == MissionStatus.Open)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

    public async Task SignUpAsync(Guid missionId, ulong userId, CancellationToken ct = default)
    {
        var mission = await db.Missions.FirstOrDefaultAsync(m => m.Id == missionId && m.Type == MissionType.EventOp, ct)
            ?? throw new InvalidOperationException("Event op not found.");

        if (!await auth.IsParticipantAsync(mission.GuildId, userId, ct))
        {
            throw new InvalidOperationException("You're not eligible to participate in this server.");
        }

        var existing = await db.MissionParticipants.AnyAsync(p => p.MissionId == missionId && p.UserId == userId, ct);
        if (existing)
        {
            return;
        }

        db.MissionParticipants.Add(new MissionParticipant
        {
            Id = Guid.NewGuid(),
            MissionId = mission.Id,
            UserId = userId,
            Status = MissionParticipantStatus.SignedUp,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Close an event op: mark signed-up members attended and award them. Returns members awarded.</summary>
    public async Task<int> CloseEventOpAsync(Guid missionId, CancellationToken ct = default)
    {
        var mission = await db.Missions
            .Include(m => m.Participants)
            .FirstOrDefaultAsync(m => m.Id == missionId && m.Type == MissionType.EventOp, ct)
            ?? throw new InvalidOperationException("Event op not found.");

        mission.Status = MissionStatus.Closed;

        var attendees = mission.Participants
            .Where(p => p.Status == MissionParticipantStatus.SignedUp || p.Status == MissionParticipantStatus.Attended)
            .ToList();

        foreach (var participant in attendees)
        {
            participant.Status = MissionParticipantStatus.Attended;
        }

        await db.SaveChangesAsync(ct);

        foreach (var participant in attendees)
        {
            await awards.AwardAsync(
                mission.GuildId, participant.UserId, mission.RewardCurrencyId, mission.RewardAmount,
                LedgerSourceType.Mission, $"op:{missionId}:user:{participant.UserId}",
                $"Event op attendance: {mission.Name}", ct);
        }

        return attendees.Count;
    }

    private async Task<MissionParticipant> GetParticipantAsync(Guid missionId, ulong userId, CancellationToken ct) =>
        await db.MissionParticipants.FirstOrDefaultAsync(p => p.MissionId == missionId && p.UserId == userId, ct)
            ?? throw new InvalidOperationException($"User {userId} has not claimed mission {missionId}.");
}
