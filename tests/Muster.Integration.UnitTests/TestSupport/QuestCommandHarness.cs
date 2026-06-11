using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Quests;
using Muster.Infrastructure.Messaging;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Quests;
using Muster.Persistence;

namespace Muster.IntegrationTests.TestSupport;

/// <summary>
/// Test driver that mirrors the old QuestBoardService surface but dispatches the real CQRS command handlers
/// (load → <see cref="IQuestAuthorizer"/> → <see cref="IQuestService"/>), so these integration tests exercise
/// the production funnel. Returns <see cref="CommandResult"/> so existing assertions (<c>IsError</c>/<c>Message</c>) hold.
/// </summary>
internal sealed class QuestCommandHarness(MusterDbContext db, GuildAuthorizationService auth, IQuestService quests, IQuestReadService reads)
{
    private readonly IQuestAuthorizer _authorizer = new QuestAuthorizer(auth, TestFeatureGates.AlwaysOn);

    public async Task<CommandResult> PostAsync(
        ulong guildId, ulong actorId, QuestOrigin origin, string name, string currency, long reward,
        string description = "", DateTimeOffset? startsAt = null, DateTimeOffset? deadline = null,
        QuestTier tier = QuestTier.None, bool requestFinalApproval = false, int capacity = 1)
        => (await PostQuestHandler.Handle(
            new PostQuest(guildId, actorId, origin, name, currency, reward, description, startsAt, deadline, tier, requestFinalApproval, capacity),
            db, auth, quests, TestFeatureGates.AlwaysOn, default)).ToCommandResult($"Quest **{name}** posted.");

    public Task<CommandResult> ClaimAsync(ulong guildId, string idRaw, ulong userId)
        => Run(idRaw, id => ClaimQuestHandler.Handle(new ClaimQuest(guildId, id, userId), db, _authorizer, quests, default), "Quest claimed.");

    public Task<CommandResult> SubmitAsync(ulong guildId, string idRaw, ulong userId, string? note = null)
        => Run(idRaw, id => SubmitQuestHandler.Handle(new SubmitQuest(guildId, id, userId, note), db, _authorizer, quests, default), "Quest submitted for review.");

    public Task<CommandResult> ApproveAsync(ulong guildId, string idRaw, ulong memberId, ulong reviewerId)
        => Run(idRaw, id => ApproveQuestSubmissionHandler.Handle(new ApproveQuestSubmission(guildId, id, memberId, reviewerId), db, _authorizer, quests, default), "Approved.");

    public Task<CommandResult> RejectAsync(ulong guildId, string idRaw, ulong memberId, ulong reviewerId)
        => Run(idRaw, id => RejectQuestSubmissionHandler.Handle(new RejectQuestSubmission(guildId, id, memberId, reviewerId), db, _authorizer, quests, default), "Rejected.");

    public Task<CommandResult> ReopenAsync(ulong guildId, string idRaw, ulong memberId, ulong reviewerId)
        => Run(idRaw, id => ReopenQuestRejectionHandler.Handle(new ReopenQuestRejection(guildId, id, memberId, reviewerId), db, _authorizer, quests, default), "Reopened.");

    public Task<CommandResult> ConfirmAsync(ulong guildId, string idRaw, ulong ownerId)
        => Run(idRaw, id => ConfirmQuestHandler.Handle(new ConfirmQuest(guildId, id, ownerId), db, _authorizer, quests, default), "Confirmed.");

    public Task<CommandResult> CancelAsync(ulong guildId, string idRaw, ulong actorId)
        => Run(idRaw, id => CancelQuestHandler.Handle(new CancelQuest(guildId, id, actorId), db, _authorizer, quests, default), "Cancelled.");

    public Task<CommandResult> DisputeAsync(ulong guildId, string idRaw, ulong userId)
        => Run(idRaw, id => DisputeQuestHandler.Handle(new DisputeQuest(guildId, id, userId), db, _authorizer, quests, default), "Dispute raised.");

    public Task<CommandResult> ArbitrateAsync(ulong guildId, string idRaw, bool pay, ulong reviewerId)
        => Run(idRaw, id => ArbitrateQuestHandler.Handle(new ArbitrateQuest(guildId, id, reviewerId, pay), db, _authorizer, quests, default), pay ? "Paid the completer." : "Refunded the owner.");

    public Task<CommandResult> RequestRevisionAsync(ulong guildId, string idRaw, ulong reviewerId, ulong? memberId = null, string? note = null)
        => Run(idRaw, id => RequestQuestRevisionHandler.Handle(new RequestQuestRevision(guildId, id, reviewerId, memberId, note), db, _authorizer, quests, default), "Sent back for revision.");

    public Task<CommandResult> AcceptIntakeAsync(ulong guildId, string idRaw, QuestTier tier, bool requireFinalApproval, ulong reviewerId)
        => Run(idRaw, id => AcceptQuestIntakeHandler.Handle(new AcceptQuestIntake(guildId, id, reviewerId, tier, requireFinalApproval), db, _authorizer, quests, default), "Accepted and opened.");

    public Task<CommandResult> RejectIntakeAsync(ulong guildId, string idRaw, ulong reviewerId)
        => Run(idRaw, id => RejectQuestIntakeHandler.Handle(new RejectQuestIntake(guildId, id, reviewerId), db, _authorizer, quests, default), "Rejected — escrow refunded.");

    public Task<CommandResult> FinalizeAsync(ulong guildId, string idRaw, bool pay, ulong reviewerId)
        => Run(idRaw, id => FinalizeQuestHandler.Handle(new FinalizeQuest(guildId, id, reviewerId, pay), db, _authorizer, quests, default), pay ? "Paid the completer." : "Refunded the owner.");

    public Task<CommandResult> EditAsync(
        ulong guildId, string idRaw, ulong actorId, string? name = null, string? description = null,
        long? reward = null, DateTimeOffset? deadline = null, QuestTier? tier = null, int? capacity = null)
        => Run(idRaw, id => EditQuestHandler.Handle(new EditQuest(guildId, id, actorId, name, description, reward, deadline, tier, capacity), db, _authorizer, quests, default), "Quest updated.");

    public Task<CommandResult> ReleaseClaimAsync(ulong guildId, string idRaw, ulong actorId, ulong targetUserId)
        => Run(idRaw, id => ReleaseQuestClaimHandler.Handle(new ReleaseQuestClaim(guildId, id, actorId, targetUserId), db, _authorizer, quests, default), "Claim released.");

    public async Task<CommandResult> ListAsync(ulong guildId)
    {
        var items = await reads.GetOpenBoardAsync(guildId);
        if (items.Count == 0)
        {
            return CommandResult.Ok("No open quests right now. Post one with `/quest-post`.");
        }

        var lines = items.Select(i => $"• **{i.Name}** _[{(i.Origin == QuestOrigin.Guild ? "Guild" : "Personal")}]_ — {i.RewardAmount} {i.Code} ({i.State})");
        return CommandResult.Ok("**Quest board**\n" + string.Join("\n", lines));
    }

    private static async Task<CommandResult> Run(string idRaw, Func<Guid, Task<Result>> act, string ok)
        => Guid.TryParse(idRaw, out var id)
            ? (await act(id)).ToCommandResult(ok)
            : CommandResult.Error("That doesn't look like a valid quest id.");
}
