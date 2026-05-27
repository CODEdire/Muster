using Muster.Contracts;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Microsoft.Extensions.Logging;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>
/// Anti-staleness auto-resolution. Once a minute the sweep moves stuck quests forward using each guild's
/// configured timeouts (hours; 0 = disabled), so nothing strands money or people. Every action reuses the
/// same guarded transitions as the manual path (so the lifecycle event is published by <see cref="IQuestService"/>
/// itself), is idempotent (the status flip removes the row from the next pass), and records an audit entry
/// (actor 0 = system). A per-quest try/catch keeps one conflict (e.g. a manager acting at the same moment)
/// from aborting the whole sweep.
/// </summary>
/// <summary>The periodic quest reconciliation sweep (activate scheduled, expire deadlines, auto-resolve timeouts).
/// The scheduler depends on this interface.</summary>
public interface IQuestMaintenanceService
{
    /// <summary>Run one reconciliation pass across active guilds as of <paramref name="now"/>. Returns items resolved.</summary>
    Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default);
}

public class QuestMaintenanceService(
    MusterDbContext db,
    IQuestService quests,
    AuditService audit,
    ILogger<QuestMaintenanceService> logger) : IQuestMaintenanceService
{
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var guilds = await db.ListActiveGuildsAsync(ct);
        var resolved = 0;

        foreach (var guild in guilds)
        {
            var s = guild.Settings.Quests;
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

            if (s.DisputeTimeoutHours > 0)
            {
                resolved += await ResolveStaleDisputesAsync(guild.Id, s, now, ct);
            }
        }

        return resolved;
    }

    /// <summary>Auto-resolve disputes left unattended past the cutoff. Fair default: the timeout favours the
    /// party that did NOT raise the dispute (the disputant bears the burden) — owner-raised pays the completer,
    /// taker-raised refunds the owner. The system arbiter (0) is exempt from recusal.</summary>
    private async Task<int> ResolveStaleDisputesAsync(ulong guildId, QuestSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(s.DisputeTimeoutHours);
        var stale = await db.ListStaleDisputedAsync(guildId, cutoff, ct);

        var count = 0;
        foreach (var quest in stale)
        {
            var taker = quest.Participants.FirstOrDefault(p => p.Status == QuestParticipantStatus.Submitted);
            var pay = quest.DisputedBy != taker?.UserId; // not the taker's dispute ⇒ pay the completer
            var label = pay ? "dispute.payCompleter" : "dispute.refundOwner";
            if (await RunAsync(guildId, quest.Id, quest.Name, label, () => quests.ArbitrateAsync(quest.Id, 0, pay, ct)))
            {
                count++;
            }
        }

        return count;
    }

    private async Task<int> ResolveIntakeAsync(ulong guildId, QuestSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(s.IntakeTimeoutHours);
        var stale = await db.ListStaleIntakeAsync(guildId, cutoff, ct);

        var count = 0;
        foreach (var m in stale)
        {
            var ok = await RunAsync(guildId, m.Id, m.Name, $"intake.{s.IntakeTimeoutAction}", () =>
                s.IntakeTimeoutAction == StaleIntakeAction.Accept
                    ? quests.AcceptAsync(m.Id, 0, QuestTier.None, 0, null, ct)
                    : quests.RejectIntakeAsync(m.Id, 0, ct));
            if (ok)
            {
                count++;
            }
        }

        return count;
    }

    private async Task<int> ResolveStaleClaimsAsync(ulong guildId, QuestSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(s.ClaimTimeoutHours);
        var stale = await db.ListStaleClaimsAsync(guildId, cutoff, ct);

        var count = 0;
        foreach (var quest in stale)
        {
            try
            {
                // The release + Released event live in QuestService; we just record the audit per member.
                var released = await quests.ReleaseStaleClaimsAsync(quest.Id, cutoff, ct);
                foreach (var _ in released)
                {
                    await AuditAsync(guildId, quest.Id, quest.Name, "claim.released", ct);
                }

                count += released.Count;
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Auto-release of stale claim on quest {QuestId} skipped.", quest.Id);
            }
        }

        return count;
    }

    private async Task<int> ResolveStaleSubmissionsAsync(ulong guildId, QuestSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(s.SubmissionTimeoutHours);
        var stale = await db.ListStaleSubmissionsAsync(guildId, cutoff, ct);

        var count = 0;
        foreach (var quest in stale)
        {
            var taker = quest.Participants.First(p => p.Status == QuestParticipantStatus.Submitted);
            var label = $"submission.{s.SubmissionTimeoutAction}";
            var ok = await RunAsync(guildId, quest.Id, quest.Name, label, () =>
                quest.Origin == QuestOrigin.Player
                    ? ResolvePersonalSubmission(quest, taker, s.SubmissionTimeoutAction, ct)
                    : ResolveGuildSubmission(quest, taker, s.SubmissionTimeoutAction, ct));
            if (ok)
            {
                count++;
            }
        }

        return count;
    }

    private Task<QuestResult> ResolvePersonalSubmission(GuildQuest m, QuestParticipant taker, StaleSubmissionAction action, CancellationToken ct)
        => action switch
        {
            StaleSubmissionAction.Reject => quests.RequestRevisionAsync(m.Id, m.OwnerId, null, "Auto: reviewer timed out — please revise and resubmit.", ct),
            StaleSubmissionAction.Dispute => quests.DisputeAsync(m.Id, m.OwnerId, "Auto: review timed out — escalated to arbitration.", ct),
            _ => quests.ConfirmAsync(m.Id, m.OwnerId, ct), // Approve (respects RequiresFinalApproval)
        };

    private Task<QuestResult> ResolveGuildSubmission(GuildQuest m, QuestParticipant taker, StaleSubmissionAction action, CancellationToken ct)
        // Guild quests can't be "disputed"; a Dispute setting falls back to sending it back for revision.
        => action == StaleSubmissionAction.Approve
            ? quests.ApproveAsync(m.Id, taker.UserId, 0, ct: ct)
            : quests.RequestRevisionAsync(m.Id, 0, taker.UserId, "Auto: reviewer timed out — please revise and resubmit.", ct);

    private async Task<int> ResolveStaleFinalAsync(ulong guildId, QuestSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromHours(s.FinalApprovalTimeoutHours);
        var stale = await db.ListStaleFinalAsync(guildId, cutoff, ct);

        var count = 0;
        foreach (var m in stale)
        {
            var pay = s.FinalApprovalTimeoutAction == StaleFinalAction.Approve;
            var ok = await RunAsync(guildId, m.Id, m.Name, $"final.{s.FinalApprovalTimeoutAction}",
                () => quests.FinalizeAsync(m.Id, 0, pay, ct));
            if (ok)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Run one auto-resolve action, swallowing races/invalid-state so the sweep keeps going, then audit.
    /// The lifecycle event is published by <see cref="IQuestService"/> as part of the transition.</summary>
    private async Task<bool> RunAsync(
        ulong guildId, Guid questId, string name, string action, Func<Task<QuestResult>> act)
    {
        try
        {
            var result = await act();
            if (result != QuestResult.Ok)
            {
                return false;
            }

            await AuditAsync(guildId, questId, name, action, default);
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Auto-resolve {Action} on quest {QuestId} skipped.", action, questId);
            return false;
        }
    }

    private Task AuditAsync(ulong guildId, Guid questId, string name, string action, CancellationToken ct)
        => audit.RecordAsync(guildId, 0, $"quest.auto.{action}", $"{name} ({questId})", ct);
}
