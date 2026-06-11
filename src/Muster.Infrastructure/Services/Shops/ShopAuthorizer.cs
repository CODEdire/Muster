using Muster.Contracts;
using Muster.Domain;
using Muster.Domain.Entities.Shops;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Infrastructure.Services.Shops;

/// <summary>
/// Resource-based authorization for the shop (the .NET resource-auth <i>pattern</i>, host-agnostic — a
/// <see cref="GuildActor"/>, not a <c>ClaimsPrincipal</c>). The single place the role / owner / seller rules
/// live; command handlers call an <c>AuthorizeAsync</c> overload before mutating, and the UI calls the pure
/// <see cref="Allows"/> (with cached role flags) for button visibility so the rules can't drift.
///
/// <para>Selling actions (manage store, create/edit/cancel listing) require the actor to own the store AND hold
/// the <c>ShopCreator</c> tier; moderation + arbitration require <c>ShopManager</c>; buying actions only require
/// participation (and that the actor isn't the seller).</para>
/// </summary>
public interface IShopAuthorizer
{
    /// <summary>Store-scoped actions (ManageStore, CreateListing).</summary>
    Task<bool> AuthorizeAsync(GuildActor actor, ShopStore store, ShopPermission action, CancellationToken ct = default);

    /// <summary>Listing-scoped actions (EditListing, CancelListing, Purchase, MakeOffer).</summary>
    Task<bool> AuthorizeAsync(GuildActor actor, ShopListing listing, ShopPermission action, CancellationToken ct = default);

    /// <summary>Order-scoped actions (Confirm, Dispute, MarkDelivered, seller-cancel, Arbitrate).</summary>
    Task<bool> AuthorizeAsync(GuildActor actor, ShopOrder order, ShopPermission action, CancellationToken ct = default);

    /// <summary>Guild-scoped (resourceless) actions (ManageCategories, ModerateRating, Arbitrate). Pass
    /// <paramref name="guildStore"/> = true for the open-a-store / list-an-item gate of a <b>guild</b> store, which
    /// requires ShopManager rather than ShopCreator.</summary>
    Task<bool> AuthorizeAsync(GuildActor actor, ulong guildId, ShopPermission action, bool guildStore = false, CancellationToken ct = default);

    /// <summary>The pure rule, with role + relationship flags already resolved.</summary>
    bool Allows(bool isCreator, bool isManager, bool isOwner, bool isSeller, bool isParticipant, ShopPermission action);
}

public sealed class ShopAuthorizer(GuildAuthorizationService roles, IFeatureGate features) : IShopAuthorizer
{
    // The shop must be at least reachable for this guild (platform kill-switch on + plan entitles it). A guild that
    // simply has the market switched off isn't blocked here — the service returns its own NotActive for that, which
    // keeps the precise result code/message. Only an above-the-guild block (Unavailable) hard-stops every command.
    private async Task<bool> ShopReachableAsync(ulong guildId, CancellationToken ct)
        => (await features.EvaluateAsync(guildId, PlatformFeature.Shop, ct)).CanEnable;

    // Selling-side actions on a store/listing. On a GUILD store these require ShopManager (the store is the guild's,
    // like a guild quest), not owner+creator.
    private static bool IsSellingAction(ShopPermission action) => action
        is ShopPermission.ManageStore or ShopPermission.CreateListing or ShopPermission.EditListing
        or ShopPermission.CancelListing or ShopPermission.RespondOffer or ShopPermission.MarkDelivered;

    public async Task<bool> AuthorizeAsync(GuildActor actor, ShopStore store, ShopPermission action, CancellationToken ct = default)
    {
        if (!await ShopReachableAsync(store.GuildId, ct)) { return false; }
        var isManager = await roles.IsShopManagerAsync(store.GuildId, actor.UserId, ct);

        // A guild store is manager-managed only (no member owner to delegate to).
        if (store.Origin == ShopStoreOrigin.Guild && IsSellingAction(action))
        {
            return isManager;
        }

        var isCreator = isManager || await roles.IsShopCreatorAsync(store.GuildId, actor.UserId, ct);
        var isParticipant = isCreator || await roles.IsParticipantAsync(store.GuildId, actor.UserId, ct);
        var isOwner = store.OwnerId == actor.UserId;
        return Allows(isCreator, isManager, isOwner, isSeller: isOwner, isParticipant, action);
    }

    public async Task<bool> AuthorizeAsync(GuildActor actor, ShopListing listing, ShopPermission action, CancellationToken ct = default)
    {
        if (!await ShopReachableAsync(listing.GuildId, ct)) { return false; }
        var isManager = await roles.IsShopManagerAsync(listing.GuildId, actor.UserId, ct);

        if (listing.Store?.Origin == ShopStoreOrigin.Guild && IsSellingAction(action))
        {
            return isManager;
        }

        var isCreator = isManager || await roles.IsShopCreatorAsync(listing.GuildId, actor.UserId, ct);
        var isParticipant = isCreator || await roles.IsParticipantAsync(listing.GuildId, actor.UserId, ct);
        var isSeller = listing.SellerId == actor.UserId;
        var isOwner = listing.Store?.OwnerId == actor.UserId || isSeller;
        return Allows(isCreator, isManager, isOwner, isSeller, isParticipant, action);
    }

    public async Task<bool> AuthorizeAsync(GuildActor actor, ShopOrder order, ShopPermission action, CancellationToken ct = default)
    {
        if (!await ShopReachableAsync(order.GuildId, ct)) { return false; }
        var isManager = await roles.IsShopManagerAsync(order.GuildId, actor.UserId, ct);
        var isBuyer = order.BuyerId == actor.UserId;
        var isSeller = order.SellerId == actor.UserId;
        return action switch
        {
            ShopPermission.Confirm => isBuyer,
            ShopPermission.Rate => isBuyer || isSeller,
            ShopPermission.Dispute => isBuyer || isSeller,
            ShopPermission.MarkDelivered => isSeller || isManager,
            ShopPermission.CancelListing => isSeller || isManager,  // seller-cancel a pending order
            ShopPermission.RespondOffer => isSeller || isManager,   // accept / decline a buyer's offer
            ShopPermission.Arbitrate => isManager,
            _ => false,
        };
    }

    public async Task<bool> AuthorizeAsync(GuildActor actor, ulong guildId, ShopPermission action, bool guildStore = false, CancellationToken ct = default)
    {
        if (!await ShopReachableAsync(guildId, ct)) { return false; }
        var isManager = await roles.IsShopManagerAsync(guildId, actor.UserId, ct);
        var isCreator = isManager || await roles.IsShopCreatorAsync(guildId, actor.UserId, ct);

        // Resourceless "may create" gates: opening a store / pre-checking listing creation. A guild store needs the
        // manager tier; a member store needs the creator tier (there's no owner yet to check). Resource overloads
        // enforce ownership once the store/listing exists.
        if (action is ShopPermission.ManageStore or ShopPermission.CreateListing)
        {
            return guildStore ? isManager : isCreator;
        }

        // Global taxonomy is admin-only: managers moderate within the rules + categories the admin sets, but don't
        // change them. (Settings live on an Admin-gated page; this is the command-level backstop for API/bot too.)
        if (action is ShopPermission.ManageCategories)
        {
            return await roles.IsAdminAsync(guildId, actor.UserId, ct);
        }

        var isParticipant = isCreator || await roles.IsParticipantAsync(guildId, actor.UserId, ct);
        return Allows(isCreator, isManager, isOwner: false, isSeller: false, isParticipant, action);
    }

    public bool Allows(bool isCreator, bool isManager, bool isOwner, bool isSeller, bool isParticipant, ShopPermission action)
        => action switch
        {
            // Moderation + arbitration — shop managers. (ManageCategories is admin-only, resolved in the
            // guild-scoped overload above.)
            ShopPermission.Arbitrate or ShopPermission.ModerateRating => isManager,

            // Selling: own the store (or moderate it) AND hold the creator tier.
            ShopPermission.ManageStore or ShopPermission.CreateListing or ShopPermission.EditListing
                or ShopPermission.CancelListing or ShopPermission.RespondOffer
                or ShopPermission.MarkDelivered => (isOwner && isCreator) || isManager,

            // Buy-now: any participant — including the seller (commission still burns; no coins are minted).
            ShopPermission.Purchase => isParticipant,
            // Offers are a negotiation with the other party, so the seller can't offer on their own listing.
            ShopPermission.MakeOffer => isParticipant && !isSeller,

            ShopPermission.ViewStore => isParticipant,

            // Confirm / Dispute / Rate are order-party checks resolved by their (later) order overloads.
            _ => false,
        };
}
