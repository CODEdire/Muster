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
public class BountyService(MusterDbContext db, EscrowService escrow, GuildAuthorizationService auth)
{
    private static string Key(Guid missionId) => $"bounty:{missionId}";

    public async Task<(BountyResult Result, Mission? Mission)> PostAsync(
        ulong guildId, ulong ownerId, string name, string description, Guid currencyId, long amount,
        DateTimeOffset? deadline = null, DateTimeOffset? startsAt = null, CancellationToken ct = default)
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
        var mission = new Mission
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Type = MissionType.Quest,
            Origin = MissionOrigin.Player,
            Name = name.Trim(),
            Description = (description ?? string.Empty).Trim(),
            Status = scheduled ? MissionStatus.Scheduled : MissionStatus.Open,
            CreatedBy = ownerId,
            OwnerId = ownerId,
            CreatedAt = DateTimeOffset.UtcNow,
            RewardCurrencyId = currencyId,
            RewardAmount = amount,
            EscrowAmount = amount,
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
        });
        await db.SaveChangesAsync(ct);
        return BountyResult.Ok;
    }

    public async Task<BountyResult> SubmitAsync(Guid missionId, ulong userId, CancellationToken ct = default)
    {
        var (mission, result) = await LoadBountyAsync(missionId, ct);
        if (mission is null)
        {
            return result;
        }

        var taker = mission.Participants.FirstOrDefault(p => p.UserId == userId && p.Status == MissionParticipantStatus.Claimed);
        if (taker is null)
        {
            return BountyResult.InvalidState;
        }

        taker.Status = MissionParticipantStatus.Submitted;
        taker.SubmittedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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

        await escrow.PayoutAsync(mission.GuildId, taker.UserId, mission.RewardCurrencyId, mission.EscrowAmount, Key(missionId), ct);
        taker.Status = MissionParticipantStatus.Approved;
        taker.ReviewedBy = ownerId;
        taker.ReviewedAt = DateTimeOffset.UtcNow;
        mission.Status = MissionStatus.Closed;
        mission.EscrowAmount = 0;
        await db.SaveChangesAsync(ct);
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

        if (mission.Status is not (MissionStatus.Open or MissionStatus.Scheduled)
            || mission.Participants.Any(p => p.Status == MissionParticipantStatus.Submitted))
        {
            return BountyResult.InvalidState; // after submission, confirm or dispute instead
        }

        await escrow.RefundAsync(mission.GuildId, ownerId, mission.RewardCurrencyId, mission.EscrowAmount, Key(missionId), ct);
        ReleaseTaker(mission);
        mission.Status = MissionStatus.Cancelled;
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

        mission.Status = MissionStatus.Disputed;
        await db.SaveChangesAsync(ct);
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
            await escrow.PayoutAsync(mission.GuildId, taker.UserId, mission.RewardCurrencyId, mission.EscrowAmount, Key(missionId), ct);
            taker.Status = MissionParticipantStatus.Approved;
            mission.Status = MissionStatus.Closed;
        }
        else
        {
            await escrow.RefundAsync(mission.GuildId, mission.OwnerId, mission.RewardCurrencyId, mission.EscrowAmount, Key(missionId), ct);
            ReleaseTaker(mission);
            mission.Status = MissionStatus.Cancelled;
        }

        mission.EscrowAmount = 0;
        await db.SaveChangesAsync(ct);
        return BountyResult.Ok;
    }

    /// <summary>Refund and expire open, past-deadline bounties that haven't been submitted. For a scheduled sweep.</summary>
    public async Task<int> ExpireDueAsync(ulong guildId, DateTimeOffset now, CancellationToken ct = default)
    {
        var due = await db.Missions
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Origin == MissionOrigin.Player
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Scheduled)
                && m.Deadline != null && m.Deadline < now)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var mission in due.Where(m => m.Participants.All(p => p.Status != MissionParticipantStatus.Submitted)))
        {
            await escrow.RefundAsync(mission.GuildId, mission.OwnerId, mission.RewardCurrencyId, mission.EscrowAmount, Key(mission.Id), ct);
            ReleaseTaker(mission);
            mission.Status = MissionStatus.Expired;
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
                && (m.Status == MissionStatus.Open || m.Status == MissionStatus.Scheduled))
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
