using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure;
using Muster.Infrastructure.Services;

namespace Muster.Web.Api;

public record CurrencyOpRequest(ulong UserId, long Amount, string? Reason);

/// <summary>
/// Public API under <c>/api/v1</c>, authenticated by an <c>X-Api-Key</c> header scoped to a guild.
/// Read endpoints expose scores/wallets/ledger; guarded write endpoints mint/spend currency. Endpoints
/// reuse the same domain services as the bot and web UI.
/// </summary>
public static class ApiEndpoints
{
    public static void MapMusterApi(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/guilds/{guildId}/leaderboard", async (
            ulong guildId, HttpContext http, ApiClientService clients, ScoreQueryService scores, int top = 25) =>
        {
            var error = await ApiAuth.CheckAsync(http, clients, guildId, "read:leaderboard");
            return error ?? Results.Ok(await scores.GetSeasonLeaderboardAsync(guildId, top <= 0 ? 25 : Math.Min(top, 100)));
        });

        v1.MapGet("/guilds/{guildId}/members/{userId}/wallets", async (
            ulong guildId, ulong userId, HttpContext http, ApiClientService clients, ScoreQueryService scores) =>
        {
            var error = await ApiAuth.CheckAsync(http, clients, guildId, "read:wallets");
            return error ?? Results.Ok(await scores.GetWalletsAsync(guildId, userId));
        });

        v1.MapGet("/guilds/{guildId}/ledger", async (
            ulong guildId, HttpContext http, ApiClientService clients, MusterDbContext db, int skip = 0, int take = 50) =>
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
        });

        v1.MapPost("/guilds/{guildId}/currencies/{code}/mint", async (
            ulong guildId, string code, CurrencyOpRequest body, HttpContext http, ApiClientService clients, CurrencyService currency) =>
        {
            var error = await ApiAuth.CheckAsync(http, clients, guildId, "write:currency");
            if (error is not null)
            {
                return error;
            }

            var result = await currency.MintAsync(guildId, code, body.UserId, body.Amount, body.Reason ?? "API mint");
            return ToResult(result);
        });

        v1.MapPost("/guilds/{guildId}/currencies/{code}/spend", async (
            ulong guildId, string code, CurrencyOpRequest body, HttpContext http, ApiClientService clients, CurrencyService currency) =>
        {
            var error = await ApiAuth.CheckAsync(http, clients, guildId, "write:currency");
            if (error is not null)
            {
                return error;
            }

            var result = await currency.SpendAsync(guildId, code, body.UserId, body.Amount, body.Reason ?? "API spend");
            return ToResult(result);
        });
    }

    private static IResult ToResult(CurrencyOperationResult result) => result.Status switch
    {
        CurrencyOperationStatus.Ok => Results.Ok(new { balance = result.Balance }),
        CurrencyOperationStatus.CurrencyNotFound => Results.NotFound(new { error = "currency_not_found" }),
        CurrencyOperationStatus.InsufficientFunds => Results.Json(
            new { error = "insufficient_funds", balance = result.Balance }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem("Unknown error"),
    };
}
