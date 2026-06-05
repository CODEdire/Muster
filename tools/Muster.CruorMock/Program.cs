// Cruor mock API — a standalone test double for the external "Cruor" loot/economy platform that Muster's
// currency connector pushes to. It implements the published Cruor OpenAPI (currency + auctions) over an
// in-memory store so the Muster stack can be exercised end-to-end without the real service.
//
// Everything resets on restart — this is a test fixture, not a database. Drive it from the bundled UI at
// /ui. Run-mode only via the AppHost (see CruorMockExtensions).
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Muster.CruorMock;

var builder = WebApplication.CreateBuilder(args);

// Accept 64-bit ids sent as JSON strings too. Discord snowflakes exceed JS Number precision (2^53), so the
// browser UI sends ids as quoted strings to avoid mangling them; the connector still sends raw numbers.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.NumberHandling |= JsonNumberHandling.AllowReadingFromString);

// SQLite path: container sets CRUOR_DB_PATH to a volume-mounted file (e.g. /data/cruor.db) so data
// survives restarts; standalone defaults to a local file next to the app. Ensure the directory exists.
var dbPath = builder.Configuration["CRUOR_DB_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "cruor.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

builder.Services.AddDbContextFactory<CruorDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<CruorStore>();

var app = builder.Build();

// Create the schema on first boot (no migrations — it's a fixture). Idempotent across restarts.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<IDbContextFactory<CruorDbContext>>()
        .CreateDbContext().Database.EnsureCreated();
}

app.UseStaticFiles();   // serves wwwroot/* (the UI at /index.html); "/" stays the health endpoint below

// The real service requires an `x-api-key` header on every call. We mirror that so connector auth wiring
// is exercised. If API_KEY is set we enforce an exact match; blank accepts any non-empty key.
var expectedApiKey = builder.Configuration["API_KEY"] ?? "test-key";

// Endpoint filter applied to every guarded group: rejects a missing key (422, matching FastAPI's required
// header validation) or a wrong key (401).
var requireApiKey = (EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
{
    var key = ctx.HttpContext.Request.Headers["x-api-key"].ToString();
    if (string.IsNullOrEmpty(key))
        return ValueTask.FromResult<object?>(Results.UnprocessableEntity(new { detail = "missing x-api-key header" }));
    if (!string.IsNullOrEmpty(expectedApiKey) && key != expectedApiKey)
        return ValueTask.FromResult<object?>(Results.Json(new { detail = "invalid api key" }, statusCode: 401));
    return next(ctx);
};

app.MapGet("/", () => Results.Ok(new { service = "cruor-mock", status = "ok", ui = "/ui" }));
app.MapGet("/ui", () => Results.Redirect("/index.html"));

// ===== Currency =============================================================
var currency = app.MapGroup("/currency").AddEndpointFilter(requireApiKey).WithTags("Currency");

currency.MapPost("/add-cruor", (CruorCreate body, CruorStore store) =>
{
    // cruor_amount is signed: + credits, - debits. Applied directly to the running balance.
    var (displayName, balance) = store.ApplyCruor(body.MemberId, body.DisplayName, body.CruorAmount);
    return Results.Ok(new { member_id = body.MemberId, display_name = displayName, applied = body.CruorAmount, balance });
});

currency.MapGet("/balance/{userId:long}", (long userId, CruorStore store) =>
    Results.Ok(new { user_id = userId, balance = store.GetBalance(userId) }));

// Helper (not in the Cruor spec): list all persisted balances so the UI repopulates after a refresh.
currency.MapGet("/balances", (CruorStore store) => Results.Ok(store.GetBalances()));

// ===== Auctions =============================================================
var auctions = app.MapGroup("/auctions").AddEndpointFilter(requireApiKey).WithTags("Auctions");

auctions.MapPost("/add-item", (ItemCreate body, CruorStore store) =>
    Results.Ok(store.AddItem(body)));

auctions.MapGet("/items", (CruorStore store) => Results.Ok(store.GetItems()));

auctions.MapPost("/add-auction", (AuctionCreate body, CruorStore store) =>
    store.TryAddAuction(body, out var auction)
        ? Results.Ok(auction)
        : Results.UnprocessableEntity(new { detail = $"unknown item_id {body.ItemId}" }));

auctions.MapGet("/active", (CruorStore store) => Results.Ok(store.AuctionsByStatus("active")));
auctions.MapGet("/awarded", (CruorStore store) => Results.Ok(store.AuctionsByStatus("awarded")));
auctions.MapGet("/unscheduled", (CruorStore store) => Results.Ok(store.AuctionsByStatus("unscheduled")));

auctions.MapPost("/start-auction", (StartAuctionRequest body, CruorStore store) =>
    store.TryStartAuction(body.AuctionId, body.DurationMinutes, out var auction, out var error)
        ? Results.Ok(auction)
        : Results.UnprocessableEntity(new { detail = error }));

auctions.MapPost("/close-auction", (CloseAuctionRequest body, CruorStore store) =>
    store.TryCloseAuction(body.AuctionId, out var auction, out var error)
        ? Results.Ok(auction)
        : Results.UnprocessableEntity(new { detail = error }));

auctions.MapPost("/place-bid", (BidRequest body, CruorStore store) =>
    store.TryPlaceBid(body, out var bid, out var error)
        ? Results.Ok(bid)
        : Results.UnprocessableEntity(new { detail = error }));

auctions.MapGet("/{auctionId:long}/bids", (long auctionId, CruorStore store) =>
    store.TryGetBids(auctionId, out var bids)
        ? Results.Ok(bids)
        : Results.UnprocessableEntity(new { detail = $"unknown auction_id {auctionId}" }));

app.Run();
