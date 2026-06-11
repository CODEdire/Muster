using Microsoft.EntityFrameworkCore;
using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Quests;

namespace Muster.Infrastructure.Messaging;

/// <summary>
/// CQRS command handlers for the quest board. Every surface (bot/web/api) invokes these through the
/// Wolverine bus, so they run inside the transactional/outbox pipeline and share one enforcement point:
/// the handler loads the quest, authorizes via <see cref="IQuestAuthorizer"/>, then delegates the
/// transition to <see cref="IQuestService"/>. They return a <see cref="Result"/> (reason = the domain
/// <see cref="QuestResult"/> name) which adapters map to user-facing text. Unit-testable as plain methods.
/// </summary>
public static class ApproveQuestSubmissionHandler
{
    public static async Task<Result> Handle(
        ApproveQuestSubmission command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        var actor = new GuildActor(command.GuildId, command.ReviewerId);
        if (!await authorizer.AuthorizeAsync(actor, quest, QuestPermission.Approve, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.ApproveAsync(command.QuestId, command.MemberId, command.ReviewerId, command.Note, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class RejectQuestSubmissionHandler
{
    public static async Task<Result> Handle(
        RejectQuestSubmission command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ReviewerId), quest, QuestPermission.Reject, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.RejectAsync(command.QuestId, command.MemberId, command.ReviewerId, command.Note, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class ReopenQuestRejectionHandler
{
    public static async Task<Result> Handle(
        ReopenQuestRejection command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ReviewerId), quest, QuestPermission.Reopen, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.ReopenRejectionAsync(command.QuestId, command.MemberId, command.ReviewerId, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class ClaimQuestHandler
{
    public static async Task<Result> Handle(
        ClaimQuest command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.UserId), quest, QuestPermission.Claim, ct))
        {
            return Result.Fail(nameof(QuestResult.NotEligible));
        }

        // Per-user active-claim cap (was on QuestBoardService — now in the funnel so commands can't bypass it).
        var settings = await db.GetQuestSettingsAsync(command.GuildId, ct);
        if (settings.MaxActiveClaimsPerUser > 0
            && await db.CountActiveClaimsAsync(command.GuildId, command.UserId, ct) >= settings.MaxActiveClaimsPerUser)
        {
            return Result.Fail(nameof(QuestResult.AtClaimLimit));
        }

        var result = await quests.ClaimAsync(command.QuestId, command.UserId, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class SubmitQuestHandler
{
    public static async Task<Result> Handle(
        SubmitQuest command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.UserId), quest, QuestPermission.Submit, ct))
        {
            return Result.Fail(nameof(QuestResult.NotEligible));
        }

        var result = await quests.SubmitAsync(command.QuestId, command.UserId, command.Note, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

/// <summary>Creation command — no resource to authorize yet, so it validates input, resolves the currency,
/// applies guild guardrails, gates guild posts on GuildMaster, and routes by origin. Validation failures
/// return the user-facing message directly in <see cref="Result.Status"/> (adapters show it verbatim).</summary>
public static class PostQuestHandler
{
    public static async Task<Result<Guid>> Handle(
        PostQuest c, MusterDbContext db, GuildAuthorizationService auth, IQuestService quests,
        Muster.Infrastructure.Services.Platform.IFeatureGate features, CancellationToken ct)
    {
        // Posting is a board-entry action — gated on the guild having quests fully enabled (the wind-down rule:
        // a switched-off board accepts no new quests, while in-flight ones still settle). CanEnable=false
        // (platform/billing block) is covered by this too.
        if (!await features.IsEnabledAsync(c.GuildId, Muster.Contracts.PlatformFeature.Quests, ct))
        {
            return Result<Guid>.Fail("Quests are turned off for this server.");
        }

        if (string.IsNullOrWhiteSpace(c.Name))
        {
            return Result<Guid>.Fail("Please provide a quest name.");
        }

        if (c.Reward <= 0)
        {
            return Result<Guid>.Fail("Reward must be greater than zero.");
        }

        if (c.Deadline is { } dl && c.StartsAt is { } st && dl <= st)
        {
            return Result<Guid>.Fail("The expiry must be after the start time.");
        }

        var settings = await db.GetQuestSettingsAsync(c.GuildId, ct);
        if (settings.MaxOpenQuestsPerPoster > 0
            && await db.CountActiveQuestsByPosterAsync(c.GuildId, c.ActorId, ct) >= settings.MaxOpenQuestsPerPoster)
        {
            return Result<Guid>.Fail($"You already have {settings.MaxOpenQuestsPerPoster} active quest(s) — settle or cancel one before posting another.");
        }

        var code = (c.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
        var currency = await db.FindCurrencyAsync(c.GuildId, code, ct);
        if (currency is null)
        {
            return Result<Guid>.Fail($"Unknown currency '{code}'.");
        }

        if (!currency.IsSpendable)
        {
            return Result<Guid>.Fail($"{code} can't be a quest reward — choose a spendable currency (e.g. COIN).");
        }

        // An optional quest type must belong to this guild (else ignore it rather than fail the post).
        Guid? questTypeId = c.QuestTypeId is { } tid && tid != Guid.Empty
            && await db.QuestTypes.AnyAsync(t => t.GuildId == c.GuildId && t.Id == tid, ct)
                ? c.QuestTypeId
                : null;

        QuestDraft draft;
        if (c.Origin == QuestOrigin.Guild)
        {
            if (!await auth.IsQuestManagerAsync(c.GuildId, c.ActorId, ct))
            {
                return Result<Guid>.Fail("You need to be a quest manager to post a guild quest. Choose a personal quest to fund one from your own balance.");
            }

            if (c.Capacity < 1)
            {
                return Result<Guid>.Fail("Capacity must be at least 1.");
            }

            draft = new QuestDraft(c.GuildId, c.ActorId, QuestOrigin.Guild, c.Name, c.Description, currency.Id, c.Reward,
                c.Deadline, c.StartsAt, c.Tier, settings.PointsForTier(c.Tier), c.Capacity, QuestTypeId: questTypeId);
        }
        else
        {
            if (c.Reward < 1)
            {
                return Result<Guid>.Fail("A player quest needs a reward of at least 1 to escrow.");
            }

            var requireFinal = settings.FinalApprovalMode switch
            {
                FinalApprovalMode.Forced => true,
                FinalApprovalMode.OwnerChoice => c.RequestFinalApproval,
                _ => false, // ApproverChoice is decided at intake; Off never requires it
            };

            draft = new QuestDraft(c.GuildId, c.ActorId, QuestOrigin.Player, c.Name, c.Description, currency.Id, c.Reward,
                c.Deadline, c.StartsAt, RequireIntake: settings.PersonalQuestIntakeApproval, RequireFinalApproval: requireFinal,
                QuestTypeId: questTypeId);
        }

        var (result, quest) = await quests.PostQuestAsync(draft, ct);
        return result == QuestResult.Ok
            ? Result<Guid>.Success(quest?.Id ?? Guid.Empty)
            : Result<Guid>.Fail(result.ToString());
    }
}

public static class CancelQuestHandler
{
    public static async Task<Result> Handle(
        CancelQuest command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, QuestPermission.Cancel, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = quest.Origin == QuestOrigin.Guild
            ? await quests.CancelQuestAsync(command.QuestId, ct)
            : await quests.CancelAsync(command.QuestId, command.ActorId, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class ConfirmQuestHandler
{
    public static async Task<Result> Handle(
        ConfirmQuest command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, QuestPermission.Confirm, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.ConfirmAsync(command.QuestId, command.ActorId, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class DisputeQuestHandler
{
    public static async Task<Result> Handle(
        DisputeQuest command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, QuestPermission.Dispute, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.DisputeAsync(command.QuestId, command.ActorId, command.Reason, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class ArbitrateQuestHandler
{
    public static async Task<Result> Handle(
        ArbitrateQuest command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, QuestPermission.Arbitrate, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.ArbitrateAsync(command.QuestId, command.ActorId, command.Pay, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class RequestQuestRevisionHandler
{
    public static async Task<Result> Handle(
        RequestQuestRevision command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, QuestPermission.RequestRevision, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.RequestRevisionAsync(command.QuestId, command.ActorId, command.MemberId, command.Note, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class AcceptQuestIntakeHandler
{
    public static async Task<Result> Handle(
        AcceptQuestIntake command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, QuestPermission.AcceptIntake, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var settings = await db.GetQuestSettingsAsync(command.GuildId, ct);
        var points = settings.PointsForTier(command.Tier);
        // The approver only sets the final-approval flag when the guild delegates that choice to them.
        var approverFinal = settings.FinalApprovalMode == FinalApprovalMode.ApproverChoice ? command.RequireFinalApproval : (bool?)null;

        var result = await quests.AcceptAsync(command.QuestId, command.ActorId, command.Tier, points, approverFinal, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class RejectQuestIntakeHandler
{
    public static async Task<Result> Handle(
        RejectQuestIntake command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, QuestPermission.RejectIntake, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.RejectIntakeAsync(command.QuestId, command.ActorId, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class FinalizeQuestHandler
{
    public static async Task<Result> Handle(
        FinalizeQuest command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, QuestPermission.Finalize, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.FinalizeAsync(command.QuestId, command.ActorId, command.Pay, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

/// <summary>Release a slot. Self-abandon (target == actor) needs the actor to be a taker; force-release of another
/// member needs manager — the handler picks the permission, so the single funnel enforces both cases.</summary>
public static class ReleaseQuestClaimHandler
{
    public static async Task<Result> Handle(
        ReleaseQuestClaim command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        var self = command.TargetUserId == command.ActorId;
        var permission = self ? QuestPermission.Abandon : QuestPermission.ForceRelease;
        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, permission, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.ReleaseClaimAsync(command.QuestId, command.TargetUserId, command.ActorId, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}

public static class EditQuestHandler
{
    public static async Task<Result> Handle(
        EditQuest command, MusterDbContext db, IQuestAuthorizer authorizer, IQuestService quests, CancellationToken ct)
    {
        var quest = await db.FindQuestInGuildAsync(command.GuildId, command.QuestId, ct);
        if (quest is null)
        {
            return Result.Fail(nameof(QuestResult.NotFound));
        }

        if (!await authorizer.AuthorizeAsync(new GuildActor(command.GuildId, command.ActorId), quest, QuestPermission.Edit, ct))
        {
            return Result.Fail(nameof(QuestResult.Forbidden));
        }

        var result = await quests.EditAsync(
            command.QuestId, command.Name, command.Description, command.Reward, command.Deadline, command.Tier, command.Capacity,
            command.QuestTypeId, ct);
        return result == QuestResult.Ok ? Result.Success() : Result.Fail(result.ToString());
    }
}
