using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.Contracts;
using Muster.Infrastructure;
using Wolverine;
using Wolverine.Http;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Platform;

namespace Muster.Web.Api;

public record CurrencyOpRequest(ulong UserId, long Amount, string? Reason, string? ExternalId = null);

/// <summary>Move funds between members. <c>FromUserId</c> = 0 means "from the calling key's bound actor".</summary>
public record TransferRequest(ulong ToUserId, long Amount, string? Reason, ulong FromUserId = 0);

/// <summary>Staff balance correction: a signed delta (positive mints, negative deducts).</summary>
public record AdjustRequest(ulong UserId, long Delta, string? Reason, string? ExternalId = null);

/// <summary>
/// Public API under <c>/api/v1</c>, authored as Wolverine.HTTP endpoints. The <c>X-Api-Key</c> / guild / scope
/// check is declarative via <see cref="RequireApiScopeAttribute"/> (handled by <see cref="ApiKeyMiddleware"/>),
/// so each handler is just the work. Reads expose scores/wallets/ledger; writes mint/spend currency. Endpoints
/// reuse the same domain services as the bot and web UI.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>Season POINTS leaderboard by default; pass <c>?currency=CODE</c> for any currency's top holders.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/leaderboard")]
    [RequireApiScope("read:leaderboard")]
    public static async Task<IResult> Leaderboard(ulong guildId, ICurrencyReadService scores, int top = 25, string? currency = null)
    {
        var n = top <= 0 ? 25 : Math.Min(top, 100);
        var board = string.IsNullOrWhiteSpace(currency)
            ? await scores.GetSeasonLeaderboardAsync(guildId, n)
            : await scores.GetLeaderboardAsync(guildId, currency, n);
        return Results.Ok(board);
    }

    [WolverineGet("/api/v1/guilds/{guildId}/members/{userId}/wallets")]
    [RequireApiScope("read:wallets")]
    public static async Task<IResult> Wallets(ulong guildId, ulong userId, ICurrencyReadService scores) =>
        Results.Ok(await scores.GetWalletsAsync(guildId, userId));

    [WolverineGet("/api/v1/guilds/{guildId}/ledger")]
    [RequireApiScope("read:ledger")]
    public static async Task<IResult> Ledger(ulong guildId, MusterDbContext db, int skip = 0, int take = 50) =>
        Results.Ok(await db.PagedLedgerAsync(guildId, skip, take));

    /// <summary>One member's ledger history (newest first), optionally filtered to <c>?currency=CODE</c>, paged.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/members/{userId}/ledger")]
    [RequireApiScope("read:ledger")]
    public static async Task<IResult> MemberLedger(
        ulong guildId, ulong userId, ICurrencyReadService scores, string? currency = null, int skip = 0, int take = 50) =>
        Results.Ok(await scores.GetMemberHistoryAsync(guildId, userId, currency, skip, take));

    [WolverinePost("/api/v1/guilds/{guildId}/currencies/{code}/mint")]
    [RequireApiScope("write:currency")]
    public static async Task<IResult> Mint(ulong guildId, string code, CurrencyOpRequest body, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<CurrencyChangeResult>(
            new MintCurrency(guildId, code, body.UserId, body.Amount, body.Reason ?? "API mint", body.ExternalId)));

    [WolverinePost("/api/v1/guilds/{guildId}/currencies/{code}/spend")]
    [RequireApiScope("write:currency")]
    public static async Task<IResult> Spend(ulong guildId, string code, CurrencyOpRequest body, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<CurrencyChangeResult>(
            new SpendCurrency(guildId, code, body.UserId, body.Amount, body.Reason ?? "API spend", body.ExternalId)));

    /// <summary>Member-to-member transfer. Runs as the key's bound actor, so the authorizer enforces "members move
    /// only their own wallet, staff may move anyone's". Omit <c>fromUserId</c> to move from the actor's own wallet.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/currencies/{code}/transfer")]
    [RequireApiScope("write:currency", requireActor: true)]
    public static async Task<IResult> Transfer(
        ulong guildId, string code, TransferRequest body, HttpContext http, IMessageBus bus, ICurrencyService currency)
    {
        var actor = http.ApiActor();
        var from = body.FromUserId == 0 ? actor : body.FromUserId;
        var result = await bus.InvokeAsync<Result>(
            new TransferCurrency(guildId, actor, code, from, body.ToUserId, body.Amount, body.Reason ?? "API transfer"));
        return await ToResultAsync(result, guildId, code, from, currency);
    }

    /// <summary>Staff balance correction / mint (signed delta). The authorizer requires the bound actor to be economy staff.</summary>
    [WolverinePost("/api/v1/guilds/{guildId}/currencies/{code}/adjust")]
    [RequireApiScope("write:currency", requireActor: true)]
    public static async Task<IResult> Adjust(
        ulong guildId, string code, AdjustRequest body, HttpContext http, IMessageBus bus, ICurrencyService currency)
    {
        var actor = http.ApiActor();
        var result = await bus.InvokeAsync<Result>(
            new AdjustCurrency(guildId, actor, code, body.UserId, body.Delta, body.Reason ?? "API adjust", body.ExternalId));
        return await ToResultAsync(result, guildId, code, body.UserId, currency);
    }

    [WolverineGet("/api/v1/guilds/{guildId}/currencies")]
    [RequireApiScope("read:wallets")]
    public static async Task<IResult> Currencies(ulong guildId, MusterDbContext db) =>
        Results.Ok(await db.ListCurrencySummariesAsync(guildId));

    [WolverineGet("/api/v1/guilds/{guildId}/members/{userId}/currencies/{code}/balance")]
    [RequireApiScope("read:wallets")]
    public static async Task<IResult> Balance(ulong guildId, ulong userId, string code, ICurrencyService currency)
    {
        var balance = await currency.GetBalanceAsync(guildId, code.Trim().ToUpperInvariant(), userId);
        return balance is null
            ? Results.NotFound(new { error = "currency_not_found" })
            : Results.Ok(new { balance });
    }

    /// <summary>Supply analytics for one currency (minted/removed/circulating/escrow + holder count).</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/currencies/{code}/supply")]
    [RequireApiScope("read:ledger")]
    public static async Task<IResult> Supply(ulong guildId, string code, ICurrencyReadService scores)
    {
        var supply = await scores.GetSupplyAsync(guildId, code);
        return supply is null ? Results.NotFound(new { error = "currency_not_found" }) : Results.Ok(supply);
    }

    /// <summary>Guild-wide recent ledger movements (newest first) for one currency, paged — the overview feed.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/currencies/{code}/movements")]
    [RequireApiScope("read:ledger")]
    public static async Task<IResult> Movements(
        ulong guildId, string code, ICurrencyReadService scores, int skip = 0, int take = 50) =>
        Results.Ok(await scores.GetGuildMovementsAsync(guildId, code, skip, take));

    /// <summary>The guild's admin audit trail (who minted/adjusted/sent/configured), filterable + paged.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/audit")]
    [RequireApiScope("read:audit")]
    public static async Task<IResult> Audit(
        ulong guildId, AuditService audit, string? action = null, string? search = null, int page = 1, int pageSize = 50) =>
        Results.Ok(await audit.SearchAsync(guildId, new AuditQuery(Search: search, Action: action, Page: page, PageSize: pageSize)));

    private static IResult ToResult(CurrencyChangeResult result) => result.Status switch
    {
        _ when result.Success => Results.Ok(new { balance = result.Balance }),
        "CurrencyNotFound" => Results.NotFound(new { error = "currency_not_found" }),
        "InsufficientFunds" => Results.Json(
            new { error = "insufficient_funds", balance = result.Balance }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem("Unknown error"),
    };

    /// <summary>Map a CQRS <see cref="Result"/> to HTTP, fetching the resulting balance on success (and on overdraft).</summary>
    private static async Task<IResult> ToResultAsync(Result result, ulong guildId, string code, ulong userId, ICurrencyService currency)
    {
        if (result.Ok)
        {
            var balance = await currency.GetBalanceAsync(guildId, code.Trim().ToUpperInvariant(), userId);
            return Results.Ok(new { balance });
        }

        return result.Status switch
        {
            "CurrencyNotFound" => Results.NotFound(new { error = "currency_not_found" }),
            "InsufficientFunds" => Results.Json(
                new { error = "insufficient_funds", balance = await currency.GetBalanceAsync(guildId, code.Trim().ToUpperInvariant(), userId) },
                statusCode: StatusCodes.Status409Conflict),
            "Forbidden" => Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Json(new { error = "invalid_request", detail = result.Status }, statusCode: StatusCodes.Status400BadRequest),
        };
    }
}
