using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

/// <summary>
/// The missions board. Supports both quest tasks (claim → submit → approve, rewarded on approval) and
/// event ops (sign-up → attendance). Awards go through <see cref="AwardService"/> and are idempotent
/// per (mission, user), so approving twice never double-rewards.
/// </summary>
public class MissionService(MusterDbContext db, AwardService awards)
{
    public async Task<Mission> CreateQuestAsync(
        ulong guildId, string name, string description, ulong createdBy,
        Guid rewardCurrencyId, long rewardAmount, DateTimeOffset? deadline = null,
        bool repeatable = false, bool requiresApproval = true, CancellationToken ct = default)
    {
        var mission = new Mission
        {
            Id = Guid.NewGuid(),
            GuildId = guildId,
            Type = MissionType.Quest,
            Name = name,
            Description = description,
            Status = MissionStatus.Open,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
            RewardCurrencyId = rewardCurrencyId,
            RewardAmount = rewardAmount,
            Deadline = deadline,
            IsRepeatable = repeatable,
            RequiresApproval = requiresApproval,
        };
        db.Missions.Add(mission);
        await db.SaveChangesAsync(ct);
        return mission;
    }

    /// <summary>Create a quest that rewards the guild's POINTS currency.</summary>
    public async Task<Mission> CreateQuestPointsAsync(
        ulong guildId, string name, string description, ulong createdBy, long rewardPoints,
        DateTimeOffset? deadline = null, bool repeatable = false, CancellationToken ct = default)
    {
        var points = await db.Currencies.FirstOrDefaultAsync(
            c => c.GuildId == guildId && c.Code == GuildProvisioningService.PointsCurrencyCode, ct)
            ?? throw new InvalidOperationException($"POINTS currency not provisioned for guild {guildId}.");

        return await CreateQuestAsync(guildId, name, description, createdBy, points.Id, rewardPoints, deadline, repeatable, ct: ct);
    }

    public async Task<IReadOnlyList<Mission>> ListOpenQuestsAsync(ulong guildId, CancellationToken ct = default)
        => await db.Missions
            .Where(m => m.GuildId == guildId && m.Type == MissionType.Quest && m.Status == MissionStatus.Open)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

    public async Task<MissionParticipant> ClaimAsync(Guid missionId, ulong userId, CancellationToken ct = default)
    {
        var missionExists = await db.Missions.AnyAsync(m => m.Id == missionId, ct);
        if (!missionExists)
        {
            throw new InvalidOperationException("Quest not found.");
        }

        var existing = await db.MissionParticipants
            .FirstOrDefaultAsync(p => p.MissionId == missionId && p.UserId == userId, ct);
        if (existing is not null)
        {
            return existing;
        }

        var participant = new MissionParticipant
        {
            Id = Guid.NewGuid(),
            MissionId = missionId,
            UserId = userId,
            Status = MissionParticipantStatus.Claimed,
        };
        db.MissionParticipants.Add(participant);
        await db.SaveChangesAsync(ct);
        return participant;
    }

    public async Task SubmitAsync(Guid missionId, ulong userId, CancellationToken ct = default)
    {
        var participant = await GetParticipantAsync(missionId, userId, ct);
        participant.Status = MissionParticipantStatus.Submitted;
        participant.SubmittedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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

        await awards.AwardAsync(
            mission.GuildId, userId, mission.RewardCurrencyId, mission.RewardAmount,
            LedgerSourceType.Mission, $"mission:{missionId}:user:{userId}",
            $"Quest approved: {mission.Name}", ct);
    }

    public async Task RejectAsync(Guid missionId, ulong userId, ulong reviewerId, CancellationToken ct = default)
    {
        var participant = await GetParticipantAsync(missionId, userId, ct);
        participant.Status = MissionParticipantStatus.Rejected;
        participant.ReviewedBy = reviewerId;
        participant.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<MissionParticipant> GetParticipantAsync(Guid missionId, ulong userId, CancellationToken ct) =>
        await db.MissionParticipants.FirstOrDefaultAsync(p => p.MissionId == missionId && p.UserId == userId, ct)
            ?? throw new InvalidOperationException($"User {userId} has not claimed mission {missionId}.");
}
