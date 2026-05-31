namespace Muster.Infrastructure.Services.Musters;

/// <summary>Maps a muster command <c>Result</c> status code to user-facing text. Shared by the bot (slash + button)
/// and the web admin so every surface reads identically.</summary>
public static class MusterStatusText
{
    public static string Friendly(string status) => status switch
    {
        "Ok" => "Done.",
        "Forbidden" => "You don't have permission to do that.",
        "PromptRequired" => "Give the muster a prompt (what members are checking in for).",
        "RewardNegative" => "Reward can't be negative.",
        "BadCapacity" => "Capacity must be a positive number.",
        "ChannelNotAllowed" => "That channel isn't on the allowed list for musters.",
        "CurrencyNotFound" => "That reward currency doesn't exist in this server.",
        "NotFound" => "That muster no longer exists.",
        "SessionNotFound" => "That tracking session doesn't exist.",
        "AlreadyParticipated" => "You're already checked in.",
        "Full" => "This muster is full.",
        "Expired" => "This muster has expired.",
        "Closed" => "This muster is closed.",
        "NotEligible" => "You're not eligible to check in.",
        "NotAParticipant" => "That member isn't on this muster.",
        "AlreadyClosed" => "This muster is already closed.",
        "AlreadyLinked" => "That muster is already linked to this session.",
        "NotLinked" => "That muster isn't linked to this session.",
        _ => $"Couldn't do that — {status}.",
    };
}
