using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Infrastructure.Services.Quests;

/// <summary>An action a <see cref="GuildActor"/> may attempt on a <see cref="GuildQuest"/> — the authorization unit.</summary>
public enum QuestPermission
{
    View,
    Claim,
    Submit,
    Edit,
    Cancel,
    RequestRevision,
    Approve,
    Reject,
    Reopen,
    Confirm,
    Dispute,
    Arbitrate,
    AcceptIntake,
    RejectIntake,
    Finalize,
    Abandon,      // a taker gives up their own active claim/submission
    ForceRelease, // a manager releases someone else's claim
}

/// <summary>
/// Resource-based authorization for quests — the .NET resource-auth <i>pattern</i>, host-agnostic (a
/// <see cref="GuildActor"/>, not a <c>ClaimsPrincipal</c>). The single place the role / origin / owner rules
/// live; command handlers call <see cref="AuthorizeAsync"/> before mutating, and the UI calls the pure
/// <see cref="Allows"/> (with its own cached manager flag) for button visibility, so the rules can't drift.
/// </summary>
public interface IQuestAuthorizer
{
    Task<bool> AuthorizeAsync(GuildActor actor, GuildQuest quest, QuestPermission action, CancellationToken ct = default);

    /// <summary>The pure rule, with role lookups already resolved. Does not cover Claim/Submit/View (those need
    /// an async participant-eligibility check) — they return false here and are handled by <see cref="AuthorizeAsync"/>.</summary>
    bool Allows(GuildActor actor, bool isManager, QuestOrigin origin, ulong ownerId, bool isTaker, QuestPermission action);
}

public sealed class QuestAuthorizer(GuildAuthorizationService roles, IFeatureGate features) : IQuestAuthorizer
{
    public async Task<bool> AuthorizeAsync(GuildActor actor, GuildQuest quest, QuestPermission action, CancellationToken ct = default)
    {
        // Feature gate (mirrors ShopAuthorizer.ShopReachableAsync). A guild that merely has quests switched off
        // isn't hard-blocked — in-flight work must still wind down — so most actions only require CanEnable
        // (platform kill-switch on + plan entitles it). Only NEW participation (Claim) requires the guild to be
        // fully Enabled; a guild-off board accepts no new takers but lets claimed quests be submitted, reviewed,
        // and paid out. An above-the-guild block (Unavailable) hard-stops everything.
        var verdict = await features.EvaluateAsync(quest.GuildId, PlatformFeature.Quests, ct);
        if (!verdict.CanEnable)
        {
            return false;
        }

        if (action is QuestPermission.Claim && !verdict.IsEnabled)
        {
            return false;
        }

        // Open to any eligible participant; finer business rules (one-per-member, capacity) stay in the transitions.
        if (action is QuestPermission.Claim or QuestPermission.Submit or QuestPermission.View)
        {
            return await roles.IsParticipantAsync(quest.GuildId, actor.UserId, ct);
        }

        var isManager = await roles.IsQuestManagerAsync(quest.GuildId, actor.UserId, ct);
        var isTaker = quest.Participants.Any(p => p.UserId == actor.UserId
            && p.Status is QuestParticipantStatus.Claimed or QuestParticipantStatus.Submitted or QuestParticipantStatus.RevisionRequested);

        return Allows(actor, isManager, quest.Origin, quest.OwnerId, isTaker, action);
    }

    public bool Allows(GuildActor actor, bool isManager, QuestOrigin origin, ulong ownerId, bool isTaker, QuestPermission action)
    {
        var isOwner = ownerId == actor.UserId;
        var isGuild = origin == QuestOrigin.Guild;

        return action switch
        {
            // GuildMaster-only: minting/approval, intake, final sign-off, dispute arbitration.
            QuestPermission.Approve or QuestPermission.Reject or QuestPermission.Reopen or QuestPermission.Arbitrate
                or QuestPermission.AcceptIntake or QuestPermission.RejectIntake or QuestPermission.Finalize => isManager,

            // Owner-vs-manager by origin: a guild quest is the guild's (manager), a bounty is the poster's (owner).
            QuestPermission.Edit or QuestPermission.Cancel or QuestPermission.RequestRevision => isGuild ? isManager : isOwner,

            // Personal-only owner/taker actions.
            QuestPermission.Confirm => isOwner && !isGuild,
            QuestPermission.Dispute => (isOwner || isTaker) && !isGuild,

            // Slot release: a taker may abandon their own (any origin); a manager may force-release another's.
            QuestPermission.Abandon => isTaker,
            QuestPermission.ForceRelease => isManager,

            _ => false, // Claim/Submit/View are resolved async in AuthorizeAsync
        };
    }
}
