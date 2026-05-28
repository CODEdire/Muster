using System.Text;
using Wolverine.Http;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Api;

/// <summary>
/// CSV export of the audit log. Unlike the public API (API-key auth), this is a web endpoint protected
/// by the cookie session: the signed-in user must be an admin of the guild. Honors the same filters as
/// the audit console.
/// </summary>
public static class AuditExportEndpoint
{
    [WolverineGet("/guilds/{guildId}/audit/export.csv")]
    public static async Task<IResult> Export(
        ulong guildId,
        HttpContext http,
        GuildAuthorizationService auth,
        AuditService audit,
        string? search = null,
        string? action = null,
        string? category = null,
        string? origin = null,
        string? outcome = null,
        string? correlation = null,
        ulong? target = null,
        string? from = null,
        string? to = null,
        string sort = "date",
        bool desc = true)
    {
        if (http.User.GetDiscordUserId() is not { } userId)
        {
            return Results.Challenge();
        }

        if (!await auth.IsAdminAsync(guildId, userId))
        {
            return Results.Forbid();
        }

        var query = new AuditQuery(
            Search: search,
            Action: action,
            Categories: ParseEnumList<AuditCategory>(category),
            Origins: ParseEnumList<Muster.Domain.Enums.AuditOrigin>(origin),
            Outcome: Enum.TryParse<Muster.Domain.Enums.AuditOutcome>(outcome, out var oc) ? oc : null,
            TargetUserId: target,
            CorrelationId: correlation,
            From: DateOnly.TryParse(from, out var f) ? f : null,
            To: DateOnly.TryParse(to, out var t) ? t : null,
            Sort: sort,
            Desc: desc,
            Page: 1,
            PageSize: 1);

        var rows = await audit.ExportAsync(guildId, query);

        var sb = new StringBuilder();
        sb.AppendLine("OccurredAt,Actor,ActorId,TargetId,Origin,Outcome,CorrelationId,Action,Category,Area,Details");
        foreach (var r in rows)
        {
            sb.Append(Csv(r.OccurredAt.UtcDateTime.ToString("o"))).Append(',')
              .Append(Csv(r.ActorName)).Append(',')
              .Append(Csv(r.ActorUserId.ToString())).Append(',')
              .Append(Csv(r.TargetUserId?.ToString() ?? string.Empty)).Append(',')
              .Append(Csv(r.Origin.ToString())).Append(',')
              .Append(Csv(r.Outcome.ToString())).Append(',')
              .Append(Csv(r.CorrelationId ?? string.Empty)).Append(',')
              .Append(Csv(r.Action)).Append(',')
              .Append(Csv(r.Category.ToString())).Append(',')
              .Append(Csv(r.Area)).Append(',')
              .Append(Csv(r.Details ?? string.Empty)).Append('\n');
        }

        return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"audit-{guildId}.csv");
    }

    /// <summary>Parse a comma-separated list of enum names into a typed collection, dropping unknown values.</summary>
    private static IReadOnlyCollection<T>? ParseEnumList<T>(string? csv) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var list = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<T>(s, out var v) ? (T?)v : null)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToList();
        return list.Count == 0 ? null : list;
    }

    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }

        return value;
    }
}
