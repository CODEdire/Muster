using Muster.Contracts;
using Muster.Infrastructure.Services.Shops;

namespace Muster.Infrastructure.Commands.Shops;

/// <summary>Presents a domain <see cref="ShopResult"/> as a user-facing <see cref="CommandResult"/> error.</summary>
public static class ShopResultPresentation
{
    public static CommandResult ToCommandResult(this ShopResult result) => CommandResult.Error(result switch
    {
        ShopResult.NotFound => "That shop item couldn't be found.",
        ShopResult.NotActive => "That listing isn't active.",
        ShopResult.OutOfStock => "That item is out of stock.",
        ShopResult.AlreadyPurchased => "That listing has already been bought.",
        ShopResult.OfferCapReached => "You already have the maximum number of open offers — resolve one first.",
        ShopResult.StoreCapReached => "You've reached the maximum number of stores allowed.",
        ShopResult.ListingCapReached => "You've reached the maximum number of active listings — sell or cancel one first.",
        ShopResult.Cooldown => "You're posting listings too quickly — try again shortly.",
        ShopResult.SlugTaken => "That storefront handle is already taken.",
        ShopResult.BelowFloor => "That price is below the minimum allowed.",
        ShopResult.AboveCeiling => "That price is above the maximum allowed.",
        ShopResult.InsufficientFunds => "You don't have enough balance for that purchase.",
        ShopResult.NotSpendable => "That currency can't be used in the shop.",
        ShopResult.NotSeller => "Only the seller can do that.",
        ShopResult.Forbidden => "You can't do that here.",
        ShopResult.AlreadyRated => "You've already left that rating.",
        ShopResult.RatingWindowClosed => "The rating window for that order has closed.",
        ShopResult.HasOrders => "This listing has live orders or offers — withdraw it to make changes.",
        ShopResult.FeaturedCapReached => "This store already has the maximum number of featured listings.",
        ShopResult.NotDelivered => "The seller hasn't marked this order delivered yet — you can confirm once they do.",
        ShopResult.CategoryRequired => "Please choose a category for this item before saving.",
        ShopResult.NameRequired => "Please give this item a name.",
        ShopResult.InvalidPrice => "Please set a price greater than zero.",
        _ => "That action isn't valid for this listing's current state.",
    });

    /// <summary>Map a command-handler <see cref="Result"/> to a user-facing <see cref="CommandResult"/>:
    /// success → <paramref name="okMessage"/>; failure → the matching <see cref="ShopResult"/> text.</summary>
    public static CommandResult ToCommandResult(this Result result, string okMessage)
        => result.Ok
            ? CommandResult.Ok(okMessage)
            : Enum.TryParse<ShopResult>(result.Status, out var sr) ? sr.ToCommandResult() : CommandResult.Error(result.Status);
}
