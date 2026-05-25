using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

public enum BountyResult
{
    Ok,
    NotFound,
    NotEligible,
    InvalidState,
    InsufficientFunds,
    NotSpendable,
    Forbidden,
}

/// <summary>
/// Player-bounty state machine. A bounty is a player-funded quest: the poster's coins are escrowed at
/// post time and transferred to the completer when the owner confirms (Quest Managers arbitrate
/// disputes). Every money-moving transition commits the escrow legs together with the status change in
/// a single transaction, so the state and the ledger can never diverge.
///
/// States: Open → (taken) → submitted → Closed (payout) | Cancelled/Expired (refund) | Disputed → resolved.
/// </summary>
public class BountyService(
    MusterDbContext db,
    EscrowService escrow,
    GuildAuthorizationService auth,
    AwardService awards,
    IQuestNotifier notifier,
    IQuestRewardSink rewards)
{
    private static string Key(Guid missionId) => $"bounty:{missionId}";

    private Task NotifyAsync(Mission m, QuestLifecycleEvent ev, ulong? target, string detail, CancellationToken ct)
        => notifier.NotifyAsync(new QuestNotification(m.GuildId, m.Id, m.Name, ev, target, detail), ct);

    /// <summary>After a completer is paid: fire the external reward event and notify the completer.</summary>
    private async Task CompletedAsync(Mission m, ulong completer, CancellationToken ct)
    {
        await rewards.OnCompletedAsync(new QuestCompletion(
            m.GuildId, m.Id, m.Name, completer, m.Origin, m.RewardCurrencyId, m.RewardAmount, m.BonusPoints, m.Tier), ct);
        await NotifyAsync(m, QuestLifecycleEvent.Settled, completer, "Paid the completer.", ct);
    }

    private static void SetStatus(Mission mission, MissionStatus status)
    {
        mission.Status = status;
        mission.StatusChangedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Owner sends a submitted personal quest back to the worker to revise and resubmit. No money moves.</summary>
    public async Task<BountyResult> RequestRevisionAsync(Guid missionId, ulong ownerId, string? note, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        if (mission.OwnerId != ownerId)
        {
            return BountyResult.Forbidden;
        }

        if (mission.Status != MissionStatus.Open)
        {
            return BountyResult.InvalidState;
        }

        var taker = mission.Participants.FirstOrDefault(p => p.Status == MissionParticipantStatus.Submitted);
        if (taker is null)
        {
            return BountyResult.InvalidState;
        }

        var max = (await db.Guilds.FirstOrDefaultAsync(g => g.Id == mission.GuildId, ct))?.Settings.MaxRevisions ?? 0;
        if (max > 0 && taker.RevisionCount >= max)
        {
            return BountyResult.InvalidState; // out of revisions — confirm or dispute instead
        }

        taker.Status = MissionParticipantStatus.RevisionRequested;
        taker.ReviewNote = note;
        taker.RevisionCount++;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.RevisionRequested, taker.UserId, note ?? "Please revise and resubmit.", ct);
        return BountyResult.Ok;
    }

    /// <summary>
    /// Quest manager accepts a pending personal quest at intake: assigns the difficulty tier (and its bonus
    /// POINTS) and opens it for takers. Optionally sets the final-approval requirement when the guild lets the
    /// approver decide. No money moves (escrow was already reserved at post). Authorization is enforced by the caller.
    /// </summary>
    public async Task<BountyResult> AcceptAsync(
        Guid missionId, ulong reviewerId, QuestTier tier, long bonusPoints, bool? requireFinalApproval, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        if (mission.Status != MissionStatus.PendingApproval)
        {
            return BountyResult.InvalidState;
        }

        mission.Tier = tier;
        mission.BonusPoints = bonusPoints;
        if (requireFinalApproval is { } rf)
        {
            mission.RequiresFinalApproval = rf;
        }

        var scheduled = mission.ScheduledStart is { } s && s > DateTimeOffset.UtcNow;
        SetStatus(mission, scheduled ? MissionStatus.Scheduled : MissionStatus.Open);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.Accepted, mission.OwnerId, "Accepted and opened for takers.", ct);
        return BountyResult.Ok;
    }

    /// <summary>Quest manager rejects a personal quest at intake → refund the owner's escrow and cancel.</summary>
    public async Task<BountyResult> RejectIntakeAsync(Guid missionId, ulong reviewerId, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        if (mission.Status != MissionStatus.PendingApproval)
        {
            return BountyResult.InvalidState;
        }

        await escrow.RefundAsync(mission.GuildId, mission.OwnerId, mission.RewardCurrencyId, mission.EscrowAmount, Key(missionId), ct);
        ReleaseTaker(mission);
        SetStatus(mission, MissionStatus.Cancelled);
        mission.EscrowAmount = 0;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.RejectedAtIntake, mission.OwnerId, "Rejected at intake — escrow refunded.", ct);
        return BountyResult.Ok;
    }

    /// <summary>Quest manager finalizes a personal quest awaiting sign-off: pay the completer (+ bonus) or refund the owner.</summary>
    public async Task<BountyResult> FinalizeAsync(Guid missionId, ulong reviewerId, bool pay, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        if (mission.Status != MissionStatus.PendingFinal)
        {
            return BountyResult.InvalidState;
        }

        var taker = mission.Participants.FirstOrDefault(p => p.Status == MissionParticipantStatus.Submitted);
        if (pay && taker is not null)
        {
            await PayAndBonusAsync(mission, taker, reviewerId, ct);
            await db.SaveChangesAsync(ct);
            await CompletedAsync(mission, taker.UserId, ct);
            return BountyResult.Ok;
        }

        await escrow.RefundAsync(mission.GuildId, mission.OwnerId, mission.RewardCurrencyId, mission.EscrowAmount, Key(missionId), ct);
        ReleaseTaker(mission);
        SetStatus(mission, MissionStatus.Cancelled);
        mission.EscrowAmount = 0;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.Refunded, mission.OwnerId, "Final sign-off declined — owner refunded.", ct);
        return BountyResult.Ok;
    }

    /// <summary>Pay the completer the escrowed reward plus any tier bonus POINTS, and close the quest. Caller saves.</summary>
    private async Task PayAndBonusAsync(Mission mission, MissionParticipant taker, ulong reviewerId, CancellationToken ct)
    {
        await escrow.PayoutAsync(mission.GuildId, taker.UserId, mission.RewardCurrencyId, mission.EscrowAmount, Key(mission.Id), ct);
        if (mission.BonusPoints > 0)
        {
            await awards.StagePointsAsync(
                mission.GuildId, taker.UserId, mission.BonusPoints, LedgerSourceType.Mission,
                $"bounty:{mission.Id}:bonus:{taker.UserId}", $"Quest bonus: {mission.Name}", ct);
        }

        taker.Status = MissionParticipantStatus.Approved;
        taker.ReviewedBy = reviewerId;
        taker.ReviewedAt = DateTimeOffset.UtcNow;
        SetStatus(mission, MissionStatus.Closed);
        mission.EscrowAmount = 0;
    }

    public async Task<(BountyResult Result, Mission? Mission)> PostAsync(
        ulong guildId, ulong ownerId, string name, string description, Guid currencyId, long amount,
        DateTimeOffset? deadline = null, DateTimeOffset? startsAt = null,
        bool requireIntake = false, bool requireFinalApproval = false, CancellationToken ct = default)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(name))
        {
            return (BountyResult.InvalidState, null);
        }

        if (!await auth.IsParticipantAsync(guildId, ownerId, ct))
        {
            return (BountyResult.NotEligible, null);
        }

        var scheduled = startsAt is { } s && s > DateTimeOffset.UtcNow;
        var status = requireIntake
            ? MissionStatus.PendingApproval
            : scheduled ? MissionStatus.Scheduled : MissionStatus.Open;
        var mission = new Mission
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Type = MissionType.Quest,
            Origin = MissionOrigin.Player,
            Name = name.Trim(),
            Description = (description ?? string.Empty).Trim(),
            Status = status,
            StatusChangedAt = DateTimeOffset.UtcNow,
            CreatedBy = ownerId,
            OwnerId = ownerId,
            CreatedAt = DateTimeOffset.UtcNow,
            RewardCurrencyId = currencyId,
            RewardAmount = amount,
            EscrowAmount = amount,
            RequiresFinalApproval = requireFinalApproval,
            ScheduledStart = startsAt,
            Deadline = deadline,
        };

        // Reserve the reward into escrow; only persist the bounty if funding succeeds (atomic).
        var hold = await escrow.HoldAsync(guildId, ownerId, currencyId, amount, Key(mission.Id), ct);
        if (hold != EscrowStatus.Ok)
        {
            return (Map(hold), null);
        }

        db.Missions.Add(mission);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission,
            mission.Status == MissionStatus.PendingApproval ? QuestLifecycleEvent.PendingApproval : QuestLifecycleEvent.Created,
            mission.Status == MissionStatus.PendingApproval ? null : mission.OwnerId,
            mission.Status == MissionStatus.PendingApproval ? "Awaiting intake approval." : "Personal quest posted.", ct);
        return (BountyResult.Ok, mission);
    }

    public async Task<BountyResult> TakeAsync(Guid missionId, ulong userId, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        if (mission.Status != MissionStatus.Open)
        {
            return BountyResult.InvalidState;
        }

        if (userId == mission.OwnerId)
        {
            return BountyResult.Forbidden; // can't take your own bounty
        }

        if (!await auth.IsParticipantAsync(mission.GuildId, userId, ct))
        {
            return BountyResult.NotEligible;
        }

        if (mission.Participants.Any(p => p.Status is MissionParticipantStatus.Claimed or MissionParticipantStatus.Submitted))
        {
            return BountyResult.InvalidState; // already taken
        }

        db.MissionParticipants.Add(new MissionParticipant
        {
            Id = Guid.NewGuid(),
            MissionId = missionId,
            UserId = userId,
            Status = MissionParticipantStatus.Claimed,
            ClaimedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return BountyResult.Ok;
    }

    public async Task<BountyResult> SubmitAsync(Guid missionId, ulong userId, string? note = null, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        var taker = mission.Participants.FirstOrDefault(p => p.UserId == userId
            && p.Status is MissionParticipantStatus.Claimed or MissionParticipantStatus.RevisionRequested);
        if (taker is null)
        {
            return BountyResult.InvalidState;
        }

        taker.Status = MissionParticipantStatus.Submitted;
        taker.SubmittedAt = DateTimeOffset.UtcNow;
        taker.Note = note;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.Submitted, mission.OwnerId, $"<@{userId}> submitted for your review.", ct);
        return BountyResult.Ok;
    }

    /// <summary>Owner confirms completion → escrow pays the completer (atomic with status change).</summary>
    public async Task<BountyResult> ConfirmAsync(Guid missionId, ulong ownerId, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        if (mission.OwnerId != ownerId)
        {
            return BountyResult.Forbidden;
        }

        if (mission.Status != MissionStatus.Open)
        {
            return BountyResult.InvalidState;
        }

        var taker = mission.Participants.FirstOrDefault(p => p.Status == MissionParticipantStatus.Submitted);
        if (taker is null)
        {
            return BountyResult.InvalidState; // nothing submitted to confirm
        }

        if (mission.RequiresFinalApproval)
        {
            // Owner accepts, but a quest manager must sign off before any money or points move.
            SetStatus(mission, MissionStatus.PendingFinal);
            await db.SaveChangesAsync(ct);
            await NotifyAsync(mission, QuestLifecycleEvent.AwaitingFinalApproval, null, "Owner accepted — awaiting a manager's final sign-off.", ct);
            return BountyResult.Ok;
        }

        await PayAndBonusAsync(mission, taker, ownerId, ct);
        await db.SaveChangesAsync(ct);
        await CompletedAsync(mission, taker.UserId, ct);
        return BountyResult.Ok;
    }

    /// <summary>Owner cancels an open bounty that hasn't been submitted → refund (atomic).</summary>
    public async Task<BountyResult> CancelAsync(Guid missionId, ulong ownerId, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        if (mission.OwnerId != ownerId)
        {
            return BountyResult.Forbidden;
        }

        if (mission.Status is not (MissionStatus.Open or MissionStatus.Scheduled or MissionStatus.PendingApproval)
            || mission.Participants.Any(p => p.Status == MissionParticipantStatus.Submitted))
        {
            return BountyResult.InvalidState; // after submission, confirm or dispute instead
        }

        await escrow.RefundAsync(mission.GuildId, ownerId, mission.RewardCurrencyId, mission.EscrowAmount, Key(missionId), ct);
        ReleaseTaker(mission);
        SetStatus(mission, MissionStatus.Cancelled);
        mission.EscrowAmount = 0;
        await db.SaveChangesAsync(ct);
        return BountyResult.Ok;
    }

    /// <summary>Owner or taker raises a dispute on a submitted bounty → Quest Manager arbitration. No money moves.</summary>
    public async Task<BountyResult> DisputeAsync(Guid missionId, ulong userId, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        var taker = mission.Participants.FirstOrDefault(p => p.Status == MissionParticipantStatus.Submitted);
        if (mission.Status != MissionStatus.Open || taker is null)
        {
            return BountyResult.InvalidState;
        }

        if (userId != mission.OwnerId && userId != taker.UserId)
        {
            return BountyResult.Forbidden;
        }

        SetStatus(mission, MissionStatus.Disputed);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.Disputed, null, "Dispute raised — a quest manager will arbitrate.", ct);
        return BountyResult.Ok;
    }

    /// <summary>Quest Manager resolves a dispute: pay the completer or refund the owner (atomic).</summary>
    public async Task<BountyResult> ArbitrateAsync(Guid missionId, bool payCompleter, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        if (mission.Status != MissionStatus.Disputed)
        {
            return BountyResult.InvalidState;
        }

        var taker = mission.Participants.FirstOrDefault(p => p.Status == MissionParticipantStatus.Submitted);

        if (payCompleter && taker is not null)
        {
            await PayAndBonusAsync(mission, taker, mission.OwnerId, ct);
            await db.SaveChangesAsync(ct);
            await CompletedAsync(mission, taker.UserId, ct);
            return BountyResult.Ok;
        }

        await escrow.RefundAsync(mission.GuildId, mission.OwnerId, mission.RewardCurrencyId, mission.EscrowAmount, Key(missionId), ct);
        ReleaseTaker(mission);
        SetStatus(mission, MissionStatus.Cancelled);
        mission.EscrowAmount = 0;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(mission, QuestLifecycleEvent.Refunded, mission.OwnerId, "Dispute resolved — owner refunded.", ct);
        return BountyResult.Ok;
    }

    /// <summary>Refund and expire open, past-deadline bounties that haven't been submitted, in one guild.</summary>
    public Task<int> ExpireDueAsync(ulong guildId, DateTimeOffset now, CancellationToken ct = default)
        => ExpireAsync(m => m.GuildId == guildId, now, ct);

    /// <summary>Refund and expire past-deadline bounties across all guilds. For the scheduled sweep.</summary>
    public Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default)
        => ExpireAsync(_ => true, now, ct);

    private async Task<int> ExpireAsync(
        System.Linq.Expressions.Expression<Func<Mission, bool>> scope, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.Missions
            .Include(m => m.Participants)
            .Where(scope)
            .Where(m => m.Origin == MissionOrigin.Player
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Scheduled || m.Status == MissionStatus.PendingApproval)
                && m.Deadline != null && m.Deadline < now)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var mission in due.Where(m => m.Participants.All(p => p.Status != MissionParticipantStatus.Submitted)))
        {
            await escrow.RefundAsync(mission.GuildId, mission.OwnerId, mission.RewardCurrencyId, mission.EscrowAmount, Key(mission.Id), ct);
            ReleaseTaker(mission);
            SetStatus(mission, MissionStatus.Expired);
            mission.EscrowAmount = 0;
            expired++;
        }

        await db.SaveChangesAsync(ct);
        return expired;
    }

    public async Task<IReadOnlyList<Mission>> ListOpenAsync(ulong guildId, CancellationToken ct = default)
        => await db.Missions
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Origin == MissionOrigin.Player
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Scheduled
                    || m.Status == MissionStatus.PendingApproval || m.Status == MissionStatus.PendingFinal))
            .OrderBy(m => m.ScheduledStart ?? m.CreatedAt)
            .ToListAsync(ct);

    private async Task<(Mission? Mission, BountyResult Result)> LoadBountyAsync(Guid missionId, CancellationToken ct)
    {
        var mission = await db.Missions.Include(m => m.Participants).FirstOrDefaultAsync(m => m.Id == missionId, ct);
        if (mission is null || mission.Origin != MissionOrigin.Player)
        {
            return (null, BountyResult.NotFound);
        }

        return (mission, BountyResult.Ok);
    }

    private static void ReleaseTaker(Mission mission)
    {
        foreach (var p in mission.Participants.Where(p => p.Status is MissionParticipantStatus.Claimed or MissionParticipantStatus.Submitted))
        {
            p.Status = MissionParticipantStatus.Rejected;
        }
    }

    private static BountyResult Map(EscrowStatus status) => status switch
    {
        EscrowStatus.InsufficientFunds => BountyResult.InsufficientFunds,
        EscrowStatus.NotSpendable => BountyResult.NotSpendable,
        EscrowStatus.CurrencyNotFound => BountyResult.NotFound,
        _ => BountyResult.InvalidState,
    };
}
