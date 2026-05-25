using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services;

namespace Muster.Infrastructure.Commands;

/// <summary>Platform-independent logic for player bounty commands. Maps <see cref="BountyResult"/> to messages.</summary>
public class BountyCommandService(BountyService bounties, MusterDbContext db)
{
    public async Task<CommandResult> PostAsync(
        ulong guildId, ulong ownerId, string name, string currencyCode, long amount, string description = "",
        DateTimeOffset? deadline = null, DateTimeOffset? startsAt = null, bool requestFinalApproval = false, CancellationToken ct = default)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.GuildId == guildId && c.Code == code, ct);
        if (currency is null)
        {
            return CommandResult.Error($"Unknown currency '{code}'.");
        }

        if (!currency.IsSpendable)
        {
            return CommandResult.Error($"{code} isn't a spendable currency — a personal quest must offer one (e.g. COIN).");
        }

        var settings = (await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct))?.Settings ?? new GuildSettings();
        var requireIntake = settings.PersonalQuestIntakeApproval;
        var requireFinal = settings.FinalApprovalMode switch
        {
            FinalApprovalMode.Forced => true,
            FinalApprovalMode.OwnerChoice => requestFinalApproval,
            _ => false, // ApproverChoice is decided at intake; Off never requires it
        };

        var (result, mission) = await bounties.PostAsync(
            guildId, ownerId, name, description, currency.Id, amount, deadline, startsAt, requireIntake, requireFinal, ct);
        if (result != BountyResult.Ok)
        {
            return Map(result);
        }

        var until = deadline is { } d ? $" — closes <t:{d.ToUnixTimeSeconds()}:R>" : "";
        var next = mission!.Status switch
        {
            MissionStatus.PendingApproval => "Sent to a quest manager for approval and difficulty tiering.",
            MissionStatus.Scheduled => "It opens for takers at its start time.",
            _ => "It can now be claimed.",
        };
        return CommandResult.Ok($"Posted personal quest **{mission.Name}** — **{amount} {code}** escrowed{until}. {next}");
    }

    /// <summary>Quest manager accepts a pending personal quest at intake: assigns a tier and opens it.</summary>
    public async Task<CommandResult> AcceptAsync(
        ulong guildId, string idRaw, QuestTier tier, bool? requireFinalApproval, ulong reviewerId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(idRaw, out var id))
        {
            return CommandResult.Error("That doesn't look like a valid quest id.");
        }

        var settings = (await db.Guilds.FirstOrDefaultAsync(g => g.Id == guildId, ct))?.Settings ?? new GuildSettings();
        var points = settings.PointsForTier(tier);
        // The approver only sets the final-approval flag when the guild delegates that choice to them.
        var approverFinal = settings.FinalApprovalMode == FinalApprovalMode.ApproverChoice ? requireFinalApproval : null;

        var result = await bounties.AcceptAsync(id, reviewerId, tier, points, approverFinal, ct);
        if (result != BountyResult.Ok)
        {
            return Map(result);
        }

        var bonus = points > 0 ? $" — bonus **{points} POINTS** (tier {tier}) on completion" : "";
        return CommandResult.Ok($"Accepted and opened for takers{bonus}.");
    }

    /// <summary>Quest manager rejects a personal quest at intake → refund the owner.</summary>
    public Task<CommandResult> RejectIntakeAsync(ulong guildId, string idRaw, ulong reviewerId, CancellationToken ct = default)
        => RunAsync(idRaw, id => bounties.RejectIntakeAsync(id, reviewerId, ct), "Rejected — the owner's escrow was refunded.");

    /// <summary>Quest manager finalizes a personal quest awaiting sign-off: pay the completer or refund the owner.</summary>
    public Task<CommandResult> FinalizeAsync(ulong guildId, string idRaw, bool pay, ulong reviewerId, CancellationToken ct = default)
        => RunAsync(idRaw, id => bounties.FinalizeAsync(id, reviewerId, pay, ct),
            pay ? "Finalized — paid the completer (with any tier bonus)." : "Finalized — refunded the owner.");

    public Task<CommandResult> TakeAsync(ulong guildId, string idRaw, ulong userId, CancellationToken ct = default)
        => RunAsync(idRaw, id => bounties.TakeAsync(id, userId, ct), "Claimed. Complete it, then `/quest-submit`.");

    public Task<CommandResult> SubmitAsync(ulong guildId, string idRaw, ulong userId, string? note = null, CancellationToken ct = default)
        => RunAsync(idRaw, id => bounties.SubmitAsync(id, userId, note, ct), "Submitted. The owner will confirm completion with `/quest-confirm`.");

    public Task<CommandResult> RequestRevisionAsync(ulong guildId, string idRaw, ulong ownerId, string? note = null, CancellationToken ct = default)
        => RunAsync(idRaw, id => bounties.RequestRevisionAsync(id, ownerId, note, ct), "Sent back to the worker to revise and resubmit.");

    public async Task<CommandResult> ConfirmAsync(ulong guildId, string idRaw, ulong ownerId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(idRaw, out var id))
        {
            return CommandResult.Error("That doesn't look like a valid quest id.");
        }

        var result = await bounties.ConfirmAsync(id, ownerId, ct);
        if (result != BountyResult.Ok)
        {
            return Map(result);
        }

        var pendingFinal = await db.Missions.Where(m => m.Id == id).Select(m => m.Status).FirstOrDefaultAsync(ct) == MissionStatus.PendingFinal;
        return CommandResult.Ok(pendingFinal
            ? "Accepted — sent to a quest manager for final sign-off before payout."
            : "Confirmed — the reward was paid to the completer.");
    }

    public Task<CommandResult> CancelAsync(ulong guildId, string idRaw, ulong ownerId, CancellationToken ct = default)
        => RunAsync(idRaw, id => bounties.CancelAsync(id, ownerId, ct), "Quest cancelled and your escrow refunded.");

    public Task<CommandResult> DisputeAsync(ulong guildId, string idRaw, ulong userId, CancellationToken ct = default)
        => RunAsync(idRaw, id => bounties.DisputeAsync(id, userId, ct), "Dispute raised — a Quest Manager will review it.");

    public Task<CommandResult> ArbitrateAsync(ulong guildId, string idRaw, bool payCompleter, CancellationToken ct = default)
        => RunAsync(idRaw, id => bounties.ArbitrateAsync(id, payCompleter, ct),
            payCompleter ? "Resolved: paid the completer." : "Resolved: refunded the owner.");

    public async Task<CommandResult> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        var open = await bounties.ListOpenAsync(guildId, ct);
        var codes = await db.Currencies.Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        if (open.Count == 0)
        {
            return CommandResult.Ok("No open bounties right now.");
        }

        var lines = open.Select(b =>
        {
            var taken = b.Participants.Any(p => p.Status is MissionParticipantStatus.Claimed or MissionParticipantStatus.Submitted);
            var until = b.Deadline is { } d ? $" · closes <t:{d.ToUnixTimeSeconds()}:R>" : "";
            return $"• **{b.Name}** — {b.RewardAmount} {codes.GetValueOrDefault(b.RewardCurrencyId, "?")}{(taken ? " (taken)" : "")}{until}";
        });
        return CommandResult.Ok("**Open bounties**\nTake one with `/bounty-take` and pick it from the list.\n" + string.Join("\n", lines));
    }

    private static async Task<CommandResult> RunAsync(string idRaw, Func<Guid, Task<BountyResult>> action, string okMessage)
    {
        if (!Guid.TryParse(idRaw, out var id))
        {
            return CommandResult.Error("That doesn't look like a valid bounty id.");
        }

        var result = await action(id);
        return result == BountyResult.Ok ? CommandResult.Ok(okMessage) : Map(result);
    }

    private static CommandResult Map(BountyResult result) => result switch
    {
        BountyResult.NotFound => CommandResult.Error("Bounty not found."),
        BountyResult.NotEligible => CommandResult.Error("You're not eligible to participate in this server."),
        BountyResult.InsufficientFunds => CommandResult.Error("You don't have enough balance to fund that bounty."),
        BountyResult.NotSpendable => CommandResult.Error("That currency can't be used for bounties."),
        BountyResult.Forbidden => CommandResult.Error("You can't do that to this bounty."),
        _ => CommandResult.Error("That action isn't valid for this bounty's current state."),
    };
}
