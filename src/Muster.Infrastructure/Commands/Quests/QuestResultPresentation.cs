using Muster.Contracts;
using Muster.Infrastructure.Services.Quests;

namespace Muster.Infrastructure.Commands.Quests;

/// <summary>Presents a domain <see cref="QuestResult"/> as a user-facing <see cref="CommandResult"/> error.</summary>
public static class QuestResultPresentation
{
    public static CommandResult ToCommandResult(this QuestResult result) => CommandResult.Error(result switch
    {
        QuestResult.NotFound => "Quest not found.",
        QuestResult.NotEligible => "You're not eligible to participate in this server.",
        QuestResult.NotOpen => "This quest isn't open.",
        QuestResult.NotStarted => "This quest hasn't started yet.",
        QuestResult.Full => "This quest is full — all slots are taken.",
        QuestResult.InsufficientFunds => "You don't have enough balance to fund that quest.",
        QuestResult.NotSpendable => "That currency can't be used for quests.",
        QuestResult.Forbidden => "You can't do that to this quest.",
        QuestResult.AlreadyParticipated => "You've already been reviewed on this quest — it's one attempt per member.",
        QuestResult.AtClaimLimit => "You're already working on the maximum number of quests — finish or drop one first.",
        _ => "That action isn't valid for this quest's current state.",
    });

    /// <summary>Map a command-handler <see cref="Result"/> to a user-facing <see cref="CommandResult"/>:
    /// success → <paramref name="okMessage"/>; failure → the matching <see cref="QuestResult"/> text.</summary>
    public static CommandResult ToCommandResult(this Result result, string okMessage)
        => result.Ok
            ? CommandResult.Ok(okMessage)
            : Enum.TryParse<QuestResult>(result.Status, out var qr) ? qr.ToCommandResult() : CommandResult.Error(result.Status);
}
