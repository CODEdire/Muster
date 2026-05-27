using System.Text;
using Wolverine.Http;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;
using Muster.Infrastructure.Services.Tracking;

namespace Muster.Web.Api;

/// <summary>
/// CSV export of per-member participation (voice minutes, messages, and points earned by source) over a
/// date range. Web endpoint protected by the cookie session: the signed-in user must be a guild admin.
/// </summary>
public static class ParticipationExportEndpoint
{
    [WolverineGet("/guilds/{guildId}/participation/export.csv")]
    public static async Task<IResult> Export(
        ulong guildId,
        HttpContext http,
        GuildAuthorizationService auth,
        ParticipationReadService participation,
        string? from = null,
        string? to = null)
    {
        if (http.User.GetDiscordUserId() is not { } userId)
        {
            return Results.Challenge();
        }

        if (!await auth.IsAdminAsync(guildId, userId))
        {
            return Results.Forbid();
        }

        var fromDate = DateOnly.TryParse(from, out var f) ? f : new DateOnly(2000, 1, 1);
        var toDate = DateOnly.TryParse(to, out var t) ? t : DateOnly.FromDateTime(DateTime.UtcNow);

        var rows = await participation.ReportAsync(guildId, fromDate, toDate);

        var sb = new StringBuilder();
        sb.AppendLine("UserId,VoiceMinutes,Messages,SessionPoints,BackgroundPoints,EventPoints,QuestPoints,MusterPoints");
        foreach (var r in rows)
        {
            sb.Append(r.UserId).Append(',')
              .Append(r.VoiceMinutes).Append(',')
              .Append(r.MessageCount).Append(',')
              .Append(r.SessionPoints).Append(',')
              .Append(r.BackgroundPoints).Append(',')
              .Append(r.EventPoints).Append(',')
              .Append(r.QuestPoints).Append(',')
              .Append(r.MusterPoints).Append('\n');
        }

        return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"participation-{guildId}.csv");
    }
}
