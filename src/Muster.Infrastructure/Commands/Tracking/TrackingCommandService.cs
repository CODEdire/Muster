
using Muster.Infrastructure.Services.Tracking;
namespace Muster.Infrastructure.Commands.Tracking;

/// <summary>Platform-independent logic for the tracking-session commands.</summary>
public class TrackingCommandService(TrackingSessionService sessions)
{
    public async Task<CommandResult> StartAsync(
        ulong guildId, ulong actorId, ulong voiceChannelId, string name, string? channelName,
        bool requireUnmuted, bool requireUndeafened, bool requireNotAlone, CancellationToken ct = default)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Manual session" : name.Trim();
        await sessions.OpenManualAsync(
            guildId, voiceChannelId, actorId, cleanName, channelName, requireUnmuted, requireUndeafened, requireNotAlone, ct);

        var skips = new List<string>();
        if (requireUnmuted) skips.Add("muted");
        if (requireUndeafened) skips.Add("deafened");
        if (requireNotAlone) skips.Add("alone");
        var guards = skips.Count == 0 ? "counting all presence" : "skipping " + string.Join("/", skips) + " members";

        return CommandResult.Ok(
            $"Started **{cleanName}** in <#{voiceChannelId}> ({guards}). Close it with `/track-stop`.");
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
