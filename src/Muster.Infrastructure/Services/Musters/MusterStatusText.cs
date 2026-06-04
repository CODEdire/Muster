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
        "BadMinimum" => "Minimum check-ins can't be negative.",
        "MinAboveCapacity" => "Minimum check-ins can't be more than the capacity.",
        "ChannelNotAllowed" => "That channel isn't on the allowed list for musters.",
        "CoinCurrencyInvalid" => "Pick a spendable currency in this server for the coin reward.",
        "TemplateNotFound" => "That template doesn't exist or is disabled.",
        "TemplateRequired" => "Pick a template — only managers can set custom rewards.",
        "NotFound" => "That muster no longer exists.",
        "SessionNotFound" => "That tracking session doesn't exist.",
        "AlreadyParticipated" => "You're already checked in.",
        "NotCheckedIn" => "You're not checked in.",
        "Full" => "This muster is full.",
        "Expired" => "This muster has expired.",
        "Closed" => "This muster is closed.",
        "NotEligible" => "You're not eligible to check in.",
        "NotAParticipant" => "That member isn't on this muster.",
        "AlreadyClosed" => "This muster is already closed.",
        "NotOpen" => "This muster isn't open.",
        "MusterClosed" => "This muster is closed — its roster is final.",
        "LinkedMuster" => "Linked musters pay + close with their session. Lock it to stop check-ins, or unlink it first to close it on its own.",
        "AlreadyLinked" => "That muster is already linked to this session.",
        "NotLinked" => "That muster isn't linked to this session.",
        _ => $"Couldn't do that — {status}.",
    };
}
