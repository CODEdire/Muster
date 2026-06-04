using System.Globalization;
using System.Text;
using Wolverine.Http;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Web.Api;

/// <summary>
/// CSV export of a single session's attendance roster (member, joined, minutes, projected/earned points).
/// Web endpoint protected by the cookie session: the signed-in user must be tracking staff (TrackingManager,
/// admin-inclusive). This matches the in-app export button, which is shown to the same tier, and the
/// session-detail view that already surfaces this data to them.
/// </summary>
public static class SessionExportEndpoint
{
    [WolverineGet("/guilds/{guildId}/guild/tracking/sessions/{sessionId}/export.csv")]
    public static async Task<IResult> Export(
        ulong guildId,
        Guid sessionId,
        HttpContext http,
        GuildAuthorizationService auth,
        ParticipationReadService participation)
    {
        if (http.User.GetDiscordUserId() is not { } userId)
        {
            return Results.Challenge();
        }

        if (!await auth.IsTrackingManagerAsync(guildId, userId))
        {
            return Results.Forbid();
        }

        var detail = await participation.SessionDetailAsync(guildId, sessionId);
        if (detail is null)
        {
            return Results.NotFound();
        }

        var sb = new StringBuilder();
        sb.AppendLine("UserId,Name,JoinedUtc,Minutes,RewardMinutes,Points");
        foreach (var m in detail.Members)
        {
            sb.Append(m.UserId).Append(',')
              .Append(Csv(m.Name)).Append(',')
              .Append(m.FirstJoinedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(m.TotalMinutes).Append(',')
              .Append(m.RewardMinutes).Append(',')
              .Append(m.RewardMinutes * detail.PointsPerMinute).Append('\n');
        }

        return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"session-{sessionId}.csv");
    }

    /// <summary>Quote a CSV field when it contains a comma, quote, or newline.</summary>
    private static string Csv(string value)
        => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
