using Muster.Infrastructure.Services;

namespace Muster.Web.Api;

/// <summary>
/// API-key authorization for the public API. Validates the <c>X-Api-Key</c> header, that the client is
/// scoped to the requested guild, and that it holds the required scope. Returns an error result to
/// short-circuit the endpoint, or null when the request is authorized.
/// </summary>
public static class ApiAuth
{
    public static async Task<IResult?> CheckAsync(HttpContext http, ApiClientService clients, ulong guildId, string scope)
    {
        var key = http.Request.Headers["X-Api-Key"].FirstOrDefault();
        var client = await clients.ValidateAsync(key ?? string.Empty);

        if (client is null)
        {
            return Results.Json(new { error = "invalid_api_key" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (client.GuildId != guildId)
        {
            return Results.Json(new { error = "guild_mismatch" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!client.Scopes.Contains(scope))
        {
            return Results.Json(new { error = "insufficient_scope", required = scope }, statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }
}
