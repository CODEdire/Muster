using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Contracts;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;
using Wolverine;
using Wolverine.Http;

namespace Muster.Web.Api;

public record CurrencyOpRequest(ulong UserId, long Amount, string? Reason);

/// <summary>
/// Public API under <c>/api/v1</c>, authored as Wolverine.HTTP endpoints and authenticated by an
/// <c>X-Api-Key</c> header scoped to a guild. Reads expose scores/wallets/ledger; writes mint/spend
/// currency. Endpoints reuse the same domain services as the bot and web UI.
/// </summary>
public static class ApiEndpoints
{
    [WolverineGet("/api/v1/guilds/{guildId}/leaderboard")]
    public static async Task<IResult> Leaderboard(
        ulong guildId, HttpContext http, ApiClientService clients, ScoreQueryService scores, int top = 25)
    {
        var error = await ApiAuth.CheckAsync(http, clients, guildId, "read:leaderboard");
        return error ?? Results.Ok(await scores.GetSeasonLeaderboardAsync(guildId, top <= 0 ? 25 : Math.Min(top, 100)));
    }

    [WolverineGet("/api/v1/guilds/{guildId}/members/{userId}/wallets")]
    public static async Task<IResult> Wallets(
        ulong guildId, ulong userId, HttpContext http, ApiClientService clients, ScoreQueryService scores)
    {
        var error = await ApiAuth.CheckAsync(http, clients, guildId, "read:wallets");
        return error ?? Results.Ok(await scores.GetWalletsAsync(guildId, userId));
    }

    [WolverineGet("/api/v1/guilds/{guildId}/ledger")]
    public static async Task<IResult> Ledger(
        ulong guildId, HttpContext http, ApiClientService clients, MusterDbContext db, int skip = 0, int take = 50)
    {
        var error = await ApiAuth.CheckAsync(http, clients, guildId, "read:ledger");
        if (error is not null)
        {
            return error;
        }

        var entries = await db.LedgerEntries
            .Where(e => e.GuildId == guildId)
            .OrderByDescending(e => e.Id)
            .Skip(Math.Max(skip, 0))
            .Take(Math.Clamp(take, 1, 100))
            .Select(e => new
            {
                e.Id,
                e.UserId,
                e.CurrencyId,
                e.SeasonId,
                e.Amount,
                SourceType = e.SourceType.ToString(),
                e.OccurredAt,
                e.Reason,
            })
            .ToListAsync();

        return Results.Ok(entries);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/currencies/{code}/mint")]
    public static async Task<IResult> Mint(
        ulong guildId, string code, CurrencyOpRequest body, HttpContext http, ApiClientService clients, IMessageBus bus)
    {
        var error = await ApiAuth.CheckAsync(http, clients, guildId, "write:currency");
        if (error is not null)
        {
            return error;
        }

        var result = await bus.InvokeAsync<CurrencyChangeResult>(
            new MintCurrency(guildId, code, body.UserId, body.Amount, body.Reason ?? "API mint"));
        return ToResult(result);
    }

    [WolverinePost("/api/v1/guilds/{guildId}/currencies/{code}/spend")]
    public static async Task<IResult> Spend(
        ulong guildId, string code, CurrencyOpRequest body, HttpContext http, ApiClientService clients, IMessageBus bus)
    {
        var error = await ApiAuth.CheckAsync(http, clients, guildId, "write:currency");
        if (error is not null)
        {
            return error;
        }

        var result = await bus.InvokeAsync<CurrencyChangeResult>(
            new SpendCurrency(guildId, code, body.UserId, body.Amount, body.Reason ?? "API spend"));
        return ToResult(result);
    }

    private static IResult ToResult(CurrencyChangeResult result) => result.Status switch
    {
        _ when result.Success => Results.Ok(new { balance = result.Balance }),
        "CurrencyNotFound" => Results.NotFound(new { error = "currency_not_found" }),
        "InsufficientFunds" => Results.Json(
            new { error = "insufficient_funds", balance = result.Balance }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem("Unknown error"),
    };
}
