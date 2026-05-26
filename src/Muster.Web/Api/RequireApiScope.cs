using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Muster.Domain.Entities;
using Muster.Infrastructure.Services.Platform;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Http;

namespace Muster.Web.Api;

/// <summary>
/// Declarative API-key gate for a Wolverine.HTTP endpoint. Placed on an endpoint method it (a) wires the
/// <see cref="ApiKeyMiddleware"/> into that endpoint's chain so the <c>X-Api-Key</c> / guild / scope check
/// runs before the handler, and (b) carries the required <paramref name="scope"/> (and whether a bound actor
/// is required) into that middleware. The middleware resolves the <see cref="ApiClient"/> once and hands it to
/// the endpoint as a parameter — so endpoints no longer repeat the auth preamble. Only endpoints carrying this
/// attribute get the middleware (cookie-auth web endpoints are untouched).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireApiScopeAttribute : ModifyChainAttribute
{
    public RequireApiScopeAttribute(string scope, bool requireActor = false)
    {
        Scope = scope;
        RequireActor = requireActor;
    }

    /// <summary>The token scope the key must hold (e.g. <c>read:quests</c>).</summary>
    public string Scope { get; }

    /// <summary>When true, the key must be bound to a Discord actor (writes that run *as* a user).</summary>
    public bool RequireActor { get; }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        if (chain is HttpChain http)
        {
            // Feed the scope + actor requirement into the middleware as compile-time constants (Wolverine can't
            // resolve them otherwise). TrySetArgument binds each by type: string → scope, bool → requireActor.
            var call = new MethodCall(typeof(ApiKeyMiddleware), nameof(ApiKeyMiddleware.LoadAsync));
            call.TrySetArgument(Constant.ForString(Scope));
            call.TrySetArgument(new Variable(typeof(bool), RequireActor ? "true" : "false"));
            http.AddMiddleware(rules, call);
        }
    }
}

/// <summary>
/// The one place API-key auth happens for the public API. Wired per-endpoint by <see cref="RequireApiScopeAttribute"/>;
/// validates the key, guild, and scope (the <b>token</b> layer), optionally requires a bound actor, and on success
/// stashes the validated <see cref="ApiClient"/> on the request (read back via <see cref="ApiKeyContext.ApiActor"/>).
/// Returning a non-<see cref="WolverineContinue"/> <see cref="IResult"/> short-circuits the request with 401/403.
/// (The client is stashed rather than returned as a handler argument so Wolverine never mistakes the
/// <see cref="ApiClient"/> type for an endpoint's JSON request body.)
/// </summary>
public static class ApiKeyMiddleware
{
    internal const string ClientItemKey = "muster.api-client";

    public static async Task<IResult> LoadAsync(
        string scope, bool requireActor, ulong guildId, HttpContext http, ApiClientService clients)
    {
        var (error, client) = await ApiAuth.ResolveAsync(http, clients, guildId, scope);
        if (error is not null)
        {
            return error;
        }

        if (requireActor && client!.ActsAsUserId == 0)
        {
            return Results.Json(
                new { error = "key_not_bound", detail = "This API key has no actor (ActsAsUserId) — it can't perform this action." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        http.Items[ClientItemKey] = client;
        return WolverineContinue.Result();
    }
}

/// <summary>Reads the API key resolved by <see cref="ApiKeyMiddleware"/> off the current request.</summary>
public static class ApiKeyContext
{
    /// <summary>The bound actor's Discord id, or 0 if the key has no actor (reads only).</summary>
    public static ulong ApiActor(this HttpContext http) =>
        (http.Items[ApiKeyMiddleware.ClientItemKey] as ApiClient)?.ActsAsUserId ?? 0;
}
