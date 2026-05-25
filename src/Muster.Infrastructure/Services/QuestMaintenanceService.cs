using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Muster.Domain.Entities;
using Muster.Domain.Enums;

namespace Muster.Infrastructure.Services;

/// <summary>
/// Anti-staleness auto-resolution. Once a minute the sweep moves stuck quests forward using each guild's
/// configured timeouts (hours; 0 = disabled), so nothing strands money or people. Every action reuses the
/// same guarded transitions as the manual path, is idempotent (the status flip removes the row from the next
/// pass), records an audit entry (actor 0 = system), and raises a lifecycle notification. A per-mission
/// try/catch keeps one conflict (e.g. a manager acting at the same moment) from aborting the whole sweep.
/// </summary>
public class QuestMaintenanceService(
    MusterDbContext db,
    BountyService bounties,
    MissionService missions,
    AuditService audit,
    IQuestNotifier notifier,
    ILogger<QuestMaintenanceService> logger)
{
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var guilds = await db.Guilds.Where(g => g.IsActive).ToListAsync(ct);
        var resolved = 0;

        foreach (var guild in guilds)
        {
            var s = guild.Settings;
            if (s.IntakeTimeoutHours > 0)
            {
                resolved += await ResolveIntakeAsync(guild.Id, s, now, ct);
            }

            if (s.ClaimTimeoutHours > 0)
            {
                resolved += await ResolveStaleClaimsAsync(guild.Id, s, now, ct);
            }

            if (s.SubmissionTimeoutHours > 0)
            {
                resolved += await ResolveStaleSubmissionsAsync(guild.Id, s, now, ct);
            }

            if (s.FinalApprovalTimeoutHours > 0)
            {
                resolved += await ResolveStaleFinalAsync(guild.Id, s, now, ct);
            }
        }

        return resolved;
    }

    private async Task<int> ResolveIntakeAsync(ulong guildId, GuildSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(s.IntakeTimeoutHours);
        var stale = await db.Missions
            .Where(m => m.GuildId == guildId && m.Origin == MissionOrigin.Player
                && m.Status == MissionStatus.PendingApproval && m.StatusChangedAt < cutoff)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct);

        var count = 0;
        foreach (var m in stale)
        {
            var ok = await RunAsync(guildId, m.Id, m.Name, $"intake.{s.IntakeTimeoutAction}", null, () =>
                s.IntakeTimeoutAction == StaleIntakeAction.Accept
                    ? bounties.AcceptAsync(m.Id, 0, QuestTier.None, 0, null, ct)
                    : bounties.RejectIntakeAsync(m.Id, 0, ct));
            if (ok)
            {
                count++;
            }
        }

        return count;
    }

    private async Task<int> ResolveStaleClaimsAsync(ulong guildId, GuildSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(s.ClaimTimeoutHours);
        var stale = await db.Missions
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Type == MissionType.Quest && m.Status == MissionStatus.Open
                && m.Participants.Any(p => p.Status == MissionParticipantStatus.Claimed && p.ClaimedAt != null && p.ClaimedAt < cutoff))
            .ToListAsync(ct);

        var count = 0;
        foreach (var mission in stale)
        {
            try
            {
                var idle = mission.Participants
                    .Where(p => p.Status == MissionParticipantStatus.Claimed && p.ClaimedAt < cutoff)
                    .ToList();
                foreach (var p in idle)
                {
                    p.Status = MissionParticipantStatus.Rejected; // released; the quest stays Open for another taker
                    p.ReviewNote = "Auto-released: claim timed out without a submission.";
                }

                await db.SaveChangesAsync(ct);
                foreach (var p in idle)
                {
                    await AuditNotifyAsync(guildId, mission.Id, mission.Name, "claim.released", p.UserId, QuestLifecycleEvent.AutoResolved, ct);
                }

                count += idle.Count;
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Auto-release of stale claim on quest {MissionId} skipped.", mission.Id);
            }
        }

        return count;
    }

    private async Task<int> ResolveStaleSubmissionsAsync(ulong guildId, GuildSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(s.SubmissionTimeoutHours);
        var stale = await db.Missions
            .Include(m => m.Participants)
            .Where(m => m.GuildId == guildId && m.Type == MissionType.Quest && m.Status == MissionStatus.Open
                && m.Participants.Any(p => p.Status == MissionParticipantStatus.Submitted && p.SubmittedAt != null && p.SubmittedAt < cutoff))
            .ToListAsync(ct);

        var count = 0;
        foreach (var mission in stale)
        {
            var taker = mission.Participants.First(p => p.Status == MissionParticipantStatus.Submitted);
            var label = $"submission.{s.SubmissionTimeoutAction}";
            var ok = await RunAsync(guildId, mission.Id, mission.Name, label, taker.UserId, () =>
                mission.Origin == MissionOrigin.Player
                    ? ResolvePersonalSubmission(mission, taker, s.SubmissionTimeoutAction, ct)
                    : ResolveGuildSubmission(mission, taker, s.SubmissionTimeoutAction, ct));
            if (ok)
            {
                count++;
            }
        }

        return count;
    }

    private Task<BountyResult> ResolvePersonalSubmission(Mission m, MissionParticipant taker, StaleSubmissionAction action, CancellationToken ct)
        => action switch
        {
            StaleSubmissionAction.Reject => bounties.RequestRevisionAsync(m.Id, m.OwnerId, "Auto: reviewer timed out — please revise and resubmit.", ct),
            StaleSubmissionAction.Dispute => bounties.DisputeAsync(m.Id, m.OwnerId, ct),
            _ => bounties.ConfirmAsync(m.Id, m.OwnerId, ct), // Approve (respects RequiresFinalApproval)
        };

    private async Task<BountyResult> ResolveGuildSubmission(Mission m, MissionParticipant taker, StaleSubmissionAction action, CancellationToken ct)
    {
        // Guild quests can't be "disputed"; a Dispute setting falls back to sending it back for revision.
        if (action == StaleSubmissionAction.Approve)
        {
            await missions.ApproveAsync(m.Id, taker.UserId, 0, ct);
        }
        else
        {
            await missions.RequestRevisionAsync(m.Id, taker.UserId, 0, "Auto: reviewer timed out — please revise and resubmit.", ct);
        }

        return BountyResult.Ok;
    }

    private async Task<int> ResolveStaleFinalAsync(ulong guildId, GuildSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(s.FinalApprovalTimeoutHours);
        var stale = await db.Missions
            .Where(m => m.GuildId == guildId && m.Origin == MissionOrigin.Player
                && m.Status == MissionStatus.PendingFinal && m.StatusChangedAt < cutoff)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct);

        var count = 0;
        foreach (var m in stale)
        {
            var pay = s.FinalApprovalTimeoutAction == StaleFinalAction.Approve;
            var ok = await RunAsync(guildId, m.Id, m.Name, $"final.{s.FinalApprovalTimeoutAction}", null,
                () => bounties.FinalizeAsync(m.Id, 0, pay, ct));
            if (ok)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Run one auto-resolve action, swallowing races/invalid-state so the sweep keeps going, then audit + notify.</summary>
    private async Task<bool> RunAsync(
        ulong guildId, Guid missionId, string name, string action, ulong? target, Func<Task<BountyResult>> act)
    {
        try
        {
            var result = await act();
            if (result != BountyResult.Ok)
            {
                return false;
            }

            await AuditNotifyAsync(guildId, missionId, name, action, target, QuestLifecycleEvent.AutoResolved, default);
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Auto-resolve {Action} on quest {MissionId} skipped.", action, missionId);
            return false;
        }
    }

    private async Task AuditNotifyAsync(
        ulong guildId, Guid missionId, string name, string action, ulong? target, QuestLifecycleEvent ev, CancellationToken ct)
    {
        await audit.RecordAsync(guildId, 0, $"quest.auto.{action}", $"{name} ({missionId})", ct);
        await notifier.NotifyAsync(new QuestNotification(guildId, missionId, name, ev, target, $"Auto-resolved: {action}"), ct);
    }
}
