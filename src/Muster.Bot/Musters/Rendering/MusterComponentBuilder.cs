using Muster.Domain.Entities.Musters;
using Muster.Domain.Enums;
using NetCord;
using NetCord.Rest;

namespace Muster.Bot.Musters.Rendering;

/// <summary>
/// Builds the action row under a muster card — a single Check-In button. Pure (no Discord/IO) so it's unit-testable.
/// The button is offered to everyone who can see the card; <b>authorization happens on click</b> (the CheckInMuster
/// command authorizes the clicker, eligibility-gated), so a button a user can't use just yields an ephemeral reason.
///
/// custom_id scheme: <c>mcheckin:{guildId}:{musterId}</c> — carries its own ids so the handler needs no state.
/// </summary>
public static class MusterComponentBuilder
{
    /// <summary>custom_id prefix / [ComponentInteraction] route for the Check-In button.</summary>
    public const string CheckIn = "mcheckin";

    public static string CheckInId(ulong guildId, Guid musterId) => $"{CheckIn}:{guildId}:{musterId}";

    /// <summary>The components under a muster card. A terminal muster carries no button; an open one shows Check-In
    /// (disabled + relabelled "Full" once a standalone muster hits its cap).</summary>
    public static IReadOnlyList<IMessageComponentProperties> Build(ReactionMuster muster)
    {
        if (muster.Status != MusterStatus.Open)
        {
            return [];
        }

        var full = muster.Capacity is { } cap && muster.SessionLinks.Count == 0 && muster.Participants.Count >= cap;

        var button = new ButtonProperties(CheckInId(muster.GuildId, muster.Id), full ? "Full" : "✅ Check in", ButtonStyle.Success)
        {
            Disabled = full,
        };

        return [new ActionRowProperties([button])];
    }
}
