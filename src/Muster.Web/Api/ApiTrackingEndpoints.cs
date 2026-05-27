using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Domain.Enums;
using Muster.Infrastructure.Commands;
using Muster.Infrastructure.Commands.Tracking;
using Muster.Infrastructure.Commands.Membership;
using Muster.Infrastructure.Services.Tracking;
using Wolverine.Http;

namespace Muster.Web.Api;

public record StartSessionRequest(ulong ChannelId, string? Name, bool SkipMuted = false, bool SkipDeafened = true, bool SkipAlone = false);
public record TrackVoiceRequest(ulong ChannelId, int PointsPerMinute = 1, int DailyCap = 0, bool RequireUnmuted = false, bool RequireUndeafened = true, bool RequireNotAlone = true);
public record TrackTextRequest(ulong ChannelId, int PointsPerMessage = 0, int MessagesPerPoint = 1, int CooldownSeconds = 0, int DailyCap = 0);
public record SetPrivacyRequest(TrackingChoice Choice);
public record CreateMultiplierRequest(
    string Kind, string? Name, decimal Factor, MultiplierScope Scope,
    DateTimeOffset? StartsAt = null, DateTimeOffset? EndsAt = null,
    WeekDays Days = WeekDays.None, TimeOnly? StartTime = null, TimeOnly? EndTime = null, ulong RoleId = 0);

/// <summary>
/// Public API for participation tracking under <c>/api/v1</c> — parity with the bot's <c>/track</c> tree and the
/// web Sessions/Multipliers pages. Reads (<c>read:tracking</c>) surface the voice leaderboard, sessions, member
/// stats, monitored channels, and multipliers; writes (<c>write:tracking</c>) open/close sessions, configure
/// channels + multipliers, and set a member's privacy. All reuse the same domain services as the bot/UI.
/// </summary>
public static class ApiTrackingEndpoints
{
    // --- Reads ---

    /// <summary>Top members by tracked voice participation time.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/tracking/leaderboard")]
    [RequireApiScope("read:tracking")]
    public static async Task<IResult> Leaderboard(ulong guildId, ParticipationReadService participation, int top = 25)
        => Results.Ok(await participation.VoiceLeaderboardAsync(guildId, top <= 0 ? 25 : Math.Min(top, 100)));

    /// <summary>Currently-active sessions (paged).</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/tracking/sessions/active")]
    [RequireApiScope("read:tracking")]
    public static async Task<IResult> ActiveSessions(ulong guildId, ParticipationReadService participation, int page = 1, int pageSize = 25)
        => Results.Ok(await participation.ActiveSessionsPageAsync(guildId, null, "started", desc: true, Page(page), Size(pageSize)));

    /// <summary>Closed session history (paged, newest first).</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/tracking/sessions")]
    [RequireApiScope("read:tracking")]
    public static async Task<IResult> SessionHistory(ulong guildId, ParticipationReadService participation, int page = 1, int pageSize = 25)
        => Results.Ok(await participation.RecentSessionsPageAsync(guildId, null, "ended", desc: true, Page(page), Size(pageSize)));

    /// <summary>One session with its attendance roster.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/tracking/sessions/{sessionId}")]
    [RequireApiScope("read:tracking")]
    public static async Task<IResult> SessionDetail(ulong guildId, Guid sessionId, ParticipationReadService participation)
    {
        var detail = await participation.SessionDetailAsync(guildId, sessionId);
        return detail is null ? Results.NotFound(new { error = "session_not_found" }) : Results.Ok(detail);
    }

    /// <summary>A member's voice participation stats (season + all-time + rank).</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/members/{userId}/tracking")]
    [RequireApiScope("read:tracking")]
    public static async Task<IResult> MemberStats(ulong guildId, ulong userId, ParticipationReadService participation)
        => Results.Ok(await participation.MemberVoiceStatsAsync(guildId, userId));

    /// <summary>The guild's monitored (configured) channels.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/tracking/channels")]
    [RequireApiScope("read:tracking")]
    public static async Task<IResult> Channels(ulong guildId, MusterDbContext db)
        => Results.Ok((await db.ListTrackedChannelsAsync(guildId)).Select(c => new
        {
            c.ChannelId, c.Name, Kind = c.Kind.ToString(), Mode = c.Mode.ToString(),
            c.PointsPerMinute, c.PointsPerMessage, c.DailyCapPoints, Guards = c.Guards.ToString(), c.DeletedAt,
        }));

    /// <summary>The guild's reward multipliers.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/tracking/multipliers")]
    [RequireApiScope("read:tracking")]
    public static async Task<IResult> Multipliers(ulong guildId, RewardMultiplierCommandService mults)
        => Results.Ok((await mults.ListAsync(guildId)).Select(m => new
        {
            m.Id, m.Name, m.Factor, Kind = m.Kind.ToString(), Scope = m.Scope.ToString(), m.Enabled,
            m.StartsAt, m.EndsAt, Days = m.Days.ToString(), m.StartTime, m.EndTime, m.RoleId,
        }));

    // --- Writes ---

    /// <summary>Open a manual tracking session. Members already in the channel are credited on the bot's next sweep.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/tracking/sessions")]
    [RequireApiScope("write:tracking", requireActor: true)]
    public static async Task<IResult> StartSession(ulong guildId, StartSessionRequest body, HttpContext http, TrackingCommandService sessions)
        => Map(await sessions.StartAsync(
            guildId, http.ApiActor(), body.ChannelId, body.Name ?? "API session", channelName: null,
            requireUnmuted: body.SkipMuted, requireUndeafened: body.SkipDeafened, requireNotAlone: body.SkipAlone));

    /// <summary>Close an active session and award attendance.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/tracking/sessions/{sessionId}/stop")]
    [RequireApiScope("write:tracking")]
    public static async Task<IResult> StopSession(ulong guildId, Guid sessionId, TrackingCommandService sessions)
        => Map(await sessions.StopAsync(guildId, sessionId.ToString()));

    /// <summary>Monitor a voice channel for background reward accrual.</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/tracking/channels/voice")]
    [RequireApiScope("write:tracking")]
    public static async Task<IResult> SetVoice(ulong guildId, TrackVoiceRequest body, TrackedChannelCommandService channels)
        => Map(await channels.SetVoiceAsync(guildId, body.ChannelId, body.PointsPerMinute, body.DailyCap,
            body.RequireUnmuted, body.RequireUndeafened, body.RequireNotAlone));

    /// <summary>Monitor a text channel (stats, or rewards when points-per-message &gt; 0).</summary>
    [WolverinePut("/api/v1/guilds/{guildId}/tracking/channels/text")]
    [RequireApiScope("write:tracking")]
    public static async Task<IResult> SetText(ulong guildId, TrackTextRequest body, TrackedChannelCommandService channels)
        => Map(await channels.SetTextAsync(guildId, body.ChannelId, body.PointsPerMessage, body.MessagesPerPoint, body.CooldownSeconds, body.DailyCap));

    /// <summary>Stop monitoring a channel.</summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/tracking/channels/{channelId}")]
    [RequireApiScope("write:tracking")]
    public static async Task<IResult> RemoveChannel(ulong guildId, ulong channelId, TrackedChannelCommandService channels)
        => Map(await channels.RemoveAsync(guildId, channelId));

    /// <summary>Set a member's tracking/privacy preference.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/members/{userId}/tracking/privacy")]
    [RequireApiScope("write:tracking")]
    public static async Task<IResult> SetPrivacy(ulong guildId, ulong userId, SetPrivacyRequest body, TrackingPreferenceCommandService prefs)
        => Map(await prefs.SetAsync(guildId, userId, body.Choice));

    /// <summary>Create a reward multiplier (one-off / recurring / role).</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/tracking/multipliers")]
    [RequireApiScope("write:tracking")]
    public static async Task<IResult> CreateMultiplier(ulong guildId, CreateMultiplierRequest body, RewardMultiplierCommandService mults)
    {
        var name = body.Name ?? string.Empty;
        var result = body.Kind?.ToLowerInvariant() switch
        {
            "recurring" when body.StartTime is { } st && body.EndTime is { } et
                => await mults.AddRecurringAsync(guildId, name, body.Factor, body.Scope, body.Days, st, et),
            "recurring" => CommandResult.Error("Recurring multipliers need startTime and endTime."),
            "role" => await mults.AddRoleAsync(guildId, name, body.Factor, body.Scope, body.RoleId),
            "oneoff" when body.StartsAt is { } s && body.EndsAt is { } e
                => await mults.AddOneOffAsync(guildId, name, body.Factor, body.Scope, s, e),
            "oneoff" => CommandResult.Error("One-off multipliers need startsAt and endsAt."),
            _ => CommandResult.Error("kind must be one of: oneoff, recurring, role."),
        };
        return Map(result);
    }

    /// <summary>Enable or disable a multiplier.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/tracking/multipliers/{id}/enabled")]
    [RequireApiScope("write:tracking")]
    public static async Task<IResult> SetMultiplierEnabled(ulong guildId, Guid id, RewardMultiplierCommandService mults, bool enabled = true)
        => Map(await mults.SetEnabledAsync(guildId, id, enabled));

    /// <summary>Remove a multiplier.</summary>
    [WolverineDelete("/api/v1/guilds/{guildId}/tracking/multipliers/{id}")]
    [RequireApiScope("write:tracking")]
    public static async Task<IResult> RemoveMultiplier(ulong guildId, Guid id, RewardMultiplierCommandService mults)
        => Map(await mults.RemoveAsync(guildId, id));

    private static int Page(int page) => page <= 0 ? 1 : page;
    private static int Size(int size) => size <= 0 ? 25 : Math.Min(size, 100);

    private static IResult Map(CommandResult result)
        => result.IsError ? Results.BadRequest(new { error = result.Message }) : Results.Ok(new { message = result.Message });
}
