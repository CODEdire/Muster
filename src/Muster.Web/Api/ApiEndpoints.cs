using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Contracts;
using Muster.Infrastructure;
using Wolverine;
using Wolverine.Http;
using Muster.Infrastructure.Services.Ledger;

namespace Muster.Web.Api;

public record CurrencyOpRequest(ulong UserId, long Amount, string? Reason);

/// <summary>
/// Public API under <c>/api/v1</c>, authored as Wolverine.HTTP endpoints. The <c>X-Api-Key</c> / guild / scope
/// check is declarative via <see cref="RequireApiScopeAttribute"/> (handled by <see cref="ApiKeyMiddleware"/>),
/// so each handler is just the work. Reads expose scores/wallets/ledger; writes mint/spend currency. Endpoints
/// reuse the same domain services as the bot and web UI.
/// </summary>
public static class ApiEndpoints
{
    [WolverineGet("/api/v1/guilds/{guildId}/leaderboard")]
    [RequireApiScope("read:leaderboard")]
    public static async Task<IResult> Leaderboard(ulong guildId, ScoreQueryService scores, int top = 25) =>
        Results.Ok(await scores.GetSeasonLeaderboardAsync(guildId, top <= 0 ? 25 : Math.Min(top, 100)));

    [WolverineGet("/api/v1/guilds/{guildId}/members/{userId}/wallets")]
    [RequireApiScope("read:wallets")]
    public static async Task<IResult> Wallets(ulong guildId, ulong userId, ScoreQueryService scores) =>
        Results.Ok(await scores.GetWalletsAsync(guildId, userId));

    [WolverineGet("/api/v1/guilds/{guildId}/ledger")]
    [RequireApiScope("read:ledger")]
    public static async Task<IResult> Ledger(ulong guildId, MusterDbContext db, int skip = 0, int take = 50)
    {
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
    [RequireApiScope("write:currency")]
    public static async Task<IResult> Mint(ulong guildId, string code, CurrencyOpRequest body, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<CurrencyChangeResult>(
            new MintCurrency(guildId, code, body.UserId, body.Amount, body.Reason ?? "API mint")));

    [WolverinePost("/api/v1/guilds/{guildId}/currencies/{code}/spend")]
    [RequireApiScope("write:currency")]
    public static async Task<IResult> Spend(ulong guildId, string code, CurrencyOpRequest body, IMessageBus bus) =>
        ToResult(await bus.InvokeAsync<CurrencyChangeResult>(
            new SpendCurrency(guildId, code, body.UserId, body.Amount, body.Reason ?? "API spend")));

    private static IResult ToResult(CurrencyChangeResult result) => result.Status switch
    {
        _ when result.Success => Results.Ok(new { balance = result.Balance }),
        "CurrencyNotFound" => Results.NotFound(new { error = "currency_not_found" }),
        "InsufficientFunds" => Results.Json(
            new { error = "insufficient_funds", balance = result.Balance }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem("Unknown error"),
    };
}
