
using Muster.Domain.Entities;
using Muster.Infrastructure.Services.Platform;
namespace Muster.Web.Api;

/// <summary>
/// API-key authorization for the public API. Validates the <c>X-Api-Key</c> header, that the client is scoped to
/// the requested guild, and that it holds the required scope — that's the <b>token</b> layer (which endpoints).
/// The returned client carries <see cref="ApiClient.ActsAsUserId"/>, the <b>actor</b> layer (whose identity +
/// guild roles the command funnel then authorizes). One DB read covers both. Endpoints don't call this directly —
/// <see cref="ApiKeyMiddleware"/> does, wired in by <see cref="RequireApiScopeAttribute"/>.
/// </summary>
public static class ApiAuth
{
    /// <summary>Authorize the key for a scope. Returns the validated client, an HTTP error result, and a stable
    /// rejection reason — the latter is used to enrich the request's telemetry span so dashboards can group
    /// rejections by cause (<c>invalid_api_key</c> vs <c>guild_mismatch</c> vs <c>insufficient_scope</c>).</summary>
    public static async Task<(IResult? Error, ApiClient? Client, string? Reason)> ResolveAsync(
        HttpContext http, ApiClientService clients, ulong guildId, string scope)
    {
        var key = http.Request.Headers["X-Api-Key"].FirstOrDefault();
        var client = await clients.ValidateAsync(key ?? string.Empty);

        if (client is null)
        {
            return (
                Results.Json(new { error = "invalid_api_key" }, statusCode: StatusCodes.Status401Unauthorized),
                null, "invalid_api_key");
        }

        if (client.GuildId != guildId)
        {
            return (
                Results.Json(new { error = "guild_mismatch" }, statusCode: StatusCodes.Status403Forbidden),
                client, "guild_mismatch");
        }

        if (!client.Scopes.Contains(scope))
        {
            return (
                Results.Json(new { error = "insufficient_scope", required = scope }, statusCode: StatusCodes.Status403Forbidden),
                client, "insufficient_scope");
        }

        return (null, client, null);
    }
}
