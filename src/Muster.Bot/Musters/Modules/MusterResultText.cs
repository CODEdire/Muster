using Muster.Contracts;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Services.Musters;

namespace Muster.Bot.Musters.Modules;

/// <summary>Adapts a muster command <see cref="Result"/> to the bot's reply types, using the shared
/// <see cref="MusterStatusText"/> map so the slash command, button, and web read identically.</summary>
public static class MusterResultText
{
    public static CommandResult ToCommandResult(Result result, string okMessage)
        => result.Ok ? CommandResult.Ok(okMessage) : CommandResult.Error(MusterStatusText.Friendly(result.Status));

    /// <summary>The ephemeral line shown to a member who clicked Check-In.</summary>
    public static string CheckIn(Result result)
        => result.Ok ? "✅ You're checked in." : MusterStatusText.Friendly(result.Status);
}
