
using Microsoft.EntityFrameworkCore;
using Muster.Domain;
using Muster.Infrastructure.Services.Tracking;
using Muster.Persistence;
namespace Muster.Infrastructure.Commands.Tracking;

/// <summary>The closed-session snapshot returned by <see cref="TrackingCommandService.StopAsync(ulong, ulong, Guid, CancellationToken)"/>
/// — carries the metadata audit needs without forcing the UI to re-read the database.</summary>
public record StoppedSessionInfo(Guid Id, string Name, ulong VoiceChannelId, string? VoiceChannelName);

/// <summary>Platform-independent logic for the tracking-session commands. Mutations gate through
/// <see cref="ITrackingAuthorizer"/> so every surface (bot/web/API) flows through the same staff check.</summary>
public class TrackingCommandService(TrackingSessionService sessions, ITrackingAuthorizer authorizer, MusterDbContext db)
{
    public async Task<CommandResult<Guid>> StartAsync(
        ulong guildId, ulong actorId, ulong voiceChannelId, string name, string? channelName,
        bool requireUnmuted, bool requireUndeafened, bool requireNotAlone, CancellationToken ct = default)
    {
        if (!await authorizer.AuthorizeAsync(new GuildActor(guildId, actorId), 0, TrackingPermission.ManageSessions, ct))
        {
            return CommandResult<Guid>.Error("Forbidden");
        }

        var cleanName = string.IsNullOrWhiteSpace(name) ? "Manual session" : name.Trim();
        var session = await sessions.OpenManualAsync(
            guildId, voiceChannelId, actorId, cleanName, channelName, requireUnmuted, requireUndeafened, requireNotAlone, ct);

        var skips = new List<string>();
        if (requireUnmuted) skips.Add("muted");
        if (requireUndeafened) skips.Add("deafened");
        if (requireNotAlone) skips.Add("alone");
        var guards = skips.Count == 0 ? "counting all presence" : "skipping " + string.Join("/", skips) + " members";

        return CommandResult<Guid>.Ok(session.Id,
            $"Started **{cleanName}** in <#{voiceChannelId}> ({guards}). Close it with `/track-stop`.");
    }

    public async Task<CommandResult> StopAsync(ulong guildId, ulong actorId, string sessionIdRaw, CancellationToken ct = default)
    {
        if (!Guid.TryParse(sessionIdRaw, out var sessionId))
        {
            return CommandResult.Error("That doesn't look like a valid session id.");
        }

        return await StopAsync(guildId, actorId, sessionId, ct);
    }

    /// <summary>Close a tracking session by id. Snapshots the session's name + voice channel before close so the
    /// audit row can carry that context without the caller re-reading the DB. Returns the snapshot on success.</summary>
    public async Task<CommandResult<StoppedSessionInfo>> StopAsync(ulong guildId, ulong actorId, Guid sessionId, CancellationToken ct = default)
    {
        if (!await authorizer.AuthorizeAsync(new GuildActor(guildId, actorId), 0, TrackingPermission.ManageSessions, ct))
        {
            return CommandResult<StoppedSessionInfo>.Error("Forbidden");
        }

        // Snapshot for audit + the post-close confirmation, then close.
        var snapshot = await db.TrackingSessions
            .Where(s => s.Id == sessionId && s.GuildId == guildId)
            .Select(s => new StoppedSessionInfo(s.Id, s.Name, s.VoiceChannelId, s.VoiceChannelName))
            .FirstOrDefaultAsync(ct);

        if (snapshot is null)
        {
            return CommandResult<StoppedSessionInfo>.Error("No tracking session with that id was found.");
        }

        var closed = await sessions.CloseAsync(sessionId, ct: ct);
        if (!closed)
        {
            return CommandResult<StoppedSessionInfo>.Error("That session is no longer active.");
        }

        return CommandResult<StoppedSessionInfo>.Ok(snapshot,
            $"Closed session `{snapshot.Name}` and awarded voice attendance.");
    }

    /// <summary>Opt a member out of one active session (for its remainder). A member may opt only themselves out;
    /// staff (admin or officer) may opt anyone out. Mirrors the web per-session opt-out.</summary>
    public async Task<CommandResult> OptOutAsync(
        ulong guildId, string sessionIdRaw, ulong actorId, ulong targetUserId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(sessionIdRaw, out var sessionId))
        {
            return CommandResult.Error("That doesn't look like a valid session id.");
        }

        // Self may opt self out; only staff may opt others. SetPrivacy permission encodes that exact rule.
        if (!await authorizer.AuthorizeAsync(new GuildActor(guildId, actorId), targetUserId, TrackingPermission.SetPrivacy, ct))
        {
            return CommandResult.Error("You can only opt yourself out of a session.");
        }

        var ok = await sessions.OptOutMemberAsync(guildId, sessionId, targetUserId, ct);
        return ok
            ? CommandResult.Ok(targetUserId == actorId
                ? "You've opted out of this session — you won't accrue further attendance in it."
                : $"<@{targetUserId}> has been opted out of this session.")
            : CommandResult.Error("No active session with that member was found.");
    }
}
