
using Muster.Infrastructure.Services.Tracking;
namespace Muster.Infrastructure.Commands;

/// <summary>Platform-independent logic for the tracking-session commands.</summary>
public class TrackingCommandService(TrackingSessionService sessions)
{
    public async Task<CommandResult> StartAsync(
        ulong guildId, ulong actorId, ulong voiceChannelId, CancellationToken ct = default)
    {
        var session = await sessions.OpenManualAsync(guildId, voiceChannelId, actorId, ct);
        return CommandResult.Ok(
            $"Started tracking session `{session.Id}` in <#{voiceChannelId}>. Close it with `/track-stop`.");
    }

    public async Task<CommandResult> StopAsync(ulong guildId, string sessionIdRaw, CancellationToken ct = default)
    {
        if (!Guid.TryParse(sessionIdRaw, out var sessionId))
        {
            return CommandResult.Error("That doesn't look like a valid session id.");
        }

        var closed = await sessions.CloseAsync(sessionId, ct: ct);
        return closed
            ? CommandResult.Ok($"Closed session `{sessionId}` and awarded voice attendance.")
            : CommandResult.Error("No tracking session with that id was found.");
    }
}
