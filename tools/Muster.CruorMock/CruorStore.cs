using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Muster.CruorMock;

// --- request schemas (match the published Cruor OpenAPI; bodies are snake_case) ----------------------
// IDs are long, not int: Discord snowflakes (member/user/holder ids) are 64-bit and overflow int → the
// JSON bind would 400. Amounts are long too (balances can run large).
public record CruorCreate(
    [property: JsonPropertyName("member_id")] long MemberId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("cruor_amount")] long CruorAmount); // signed: + credits, - debits

public record ItemCreate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("holder_id")] long HolderId);

public record AuctionCreate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("item_id")] long ItemId);

public record StartAuctionRequest(
    [property: JsonPropertyName("auction_id")] long AuctionId,
    [property: JsonPropertyName("duration_minutes")] int DurationMinutes);

public record CloseAuctionRequest(
    [property: JsonPropertyName("auction_id")] long AuctionId);

public record BidRequest(
    [property: JsonPropertyName("user_id")] long UserId,
    [property: JsonPropertyName("auction_id")] long AuctionId,
    [property: JsonPropertyName("amount")] long Amount);

/// <summary>
/// SQLite-backed store for the mock — currency balances, auction items, auctions, and bids survive restarts
/// (the DB file lives on a docker volume). A lightweight write lock serializes multi-step operations (SQLite
/// is single-writer anyway). Response views are emitted as plain objects with snake_case names to match the
/// published contract. A fresh <see cref="CruorDbContext"/> is created per operation via the factory.
/// </summary>
public sealed class CruorStore(IDbContextFactory<CruorDbContext> factory)
{
    private readonly object _writeLock = new();

    // ----- currency ---------------------------------------------------------
    public (string DisplayName, long Balance) ApplyCruor(long memberId, string displayName, long amount)
    {
        lock (_writeLock)
        {
            using var db = factory.CreateDbContext();
            var bal = db.Balances.Find(memberId);
            if (bal is null)
                db.Balances.Add(bal = new BalanceEntity { MemberId = memberId });
            bal.DisplayName = displayName;   // keep latest name
            bal.Value += amount;             // signed: applies + or -
            db.SaveChanges();
            return (bal.DisplayName, bal.Value);
        }
    }

    public long GetBalance(long userId)
    {
        using var db = factory.CreateDbContext();
        return db.Balances.Find(userId)?.Value ?? 0;
    }

    // Not in the published Cruor contract — a helper so the test UI can list persisted balances after a refresh.
    // member_id is emitted as a string: Discord snowflakes exceed JS Number precision (2^53), so a JSON number
    // would be mangled in the browser.
    public List<object> GetBalances()
    {
        using var db = factory.CreateDbContext();
        return db.Balances.AsNoTracking().OrderByDescending(b => b.Value).AsEnumerable()
            .Select(b => (object)new { member_id = b.MemberId.ToString(), display_name = b.DisplayName, balance = b.Value })
            .ToList();
    }

    // ----- items ------------------------------------------------------------
    public object AddItem(ItemCreate body)
    {
        lock (_writeLock)
        {
            using var db = factory.CreateDbContext();
            var item = new ItemEntity { Name = body.Name, Description = body.Description, Quantity = body.Quantity, HolderId = body.HolderId };
            db.Items.Add(item);
            db.SaveChanges();
            return ItemView(item);
        }
    }

    public List<object> GetItems()
    {
        using var db = factory.CreateDbContext();
        return db.Items.AsNoTracking().OrderBy(i => i.Id).AsEnumerable().Select(ItemView).ToList();
    }

    // ----- auctions ---------------------------------------------------------
    public bool TryAddAuction(AuctionCreate body, out object? auction)
    {
        lock (_writeLock)
        {
            using var db = factory.CreateDbContext();
            if (!db.Items.Any(i => i.Id == body.ItemId)) { auction = null; return false; }
            var a = new AuctionEntity { Name = body.Name, Description = body.Description, ItemId = body.ItemId };
            db.Auctions.Add(a);
            db.SaveChanges();
            auction = AuctionView(a);
            return true;
        }
    }

    public List<object> AuctionsByStatus(string status)
    {
        using var db = factory.CreateDbContext();
        return db.Auctions.AsNoTracking().OrderBy(a => a.Id).AsEnumerable()
            .Where(a => Status(a) == status).Select(AuctionView).ToList();
    }

    public bool TryStartAuction(long auctionId, int durationMinutes, out object? auction, out string? error)
    {
        lock (_writeLock)
        {
            using var db = factory.CreateDbContext();
            var a = db.Auctions.Find(auctionId);
            if (a is null) { auction = null; error = $"unknown auction_id {auctionId}"; return false; }
            if (Status(a) != "unscheduled") { auction = null; error = $"auction {auctionId} already {Status(a)}"; return false; }
            a.StartedAt = DateTimeOffset.UtcNow;
            a.EndsAt = a.StartedAt.Value.AddMinutes(durationMinutes);
            db.SaveChanges();
            auction = AuctionView(a); error = null; return true;
        }
    }

    public bool TryCloseAuction(long auctionId, out object? auction, out string? error)
    {
        lock (_writeLock)
        {
            using var db = factory.CreateDbContext();
            var a = db.Auctions.Find(auctionId);
            if (a is null) { auction = null; error = $"unknown auction_id {auctionId}"; return false; }
            if (Status(a) == "awarded") { auction = null; error = $"auction {auctionId} already awarded"; return false; }
            a.ClosedAt = DateTimeOffset.UtcNow;
            var top = db.Bids.Where(x => x.AuctionId == auctionId)
                .OrderByDescending(x => x.Amount).ThenBy(x => x.Id).FirstOrDefault();
            if (top is not null)
            {
                a.AwardedTo = top.UserId;
                a.WinningBid = top.Amount;
                var item = db.Items.Find(a.ItemId);
                if (item is not null) item.HolderId = top.UserId; // transfer to winner
            }
            db.SaveChanges();
            auction = AuctionView(a); error = null; return true;
        }
    }

    // ----- bids -------------------------------------------------------------
    public bool TryPlaceBid(BidRequest body, out object? bid, out string? error)
    {
        lock (_writeLock)
        {
            using var db = factory.CreateDbContext();
            var a = db.Auctions.Find(body.AuctionId);
            if (a is null) { bid = null; error = $"unknown auction_id {body.AuctionId}"; return false; }
            if (Status(a) != "active") { bid = null; error = $"auction {body.AuctionId} not active"; return false; }
            var b = new BidEntity { UserId = body.UserId, AuctionId = body.AuctionId, Amount = body.Amount, PlacedAt = DateTimeOffset.UtcNow };
            db.Bids.Add(b);
            db.SaveChanges();
            bid = BidView(b); error = null; return true;
        }
    }

    public bool TryGetBids(long auctionId, out List<object> bids)
    {
        using var db = factory.CreateDbContext();
        if (!db.Auctions.Any(a => a.Id == auctionId)) { bids = []; return false; }
        bids = db.Bids.AsNoTracking().Where(x => x.AuctionId == auctionId)
            .OrderBy(x => x.Id).AsEnumerable().Select(BidView).ToList();
        return true;
    }

    // ----- views + status ---------------------------------------------------
    // unscheduled -> active -> awarded, derived from timestamps.
    private static string Status(AuctionEntity a)
        => a.AwardedTo is not null || a.ClosedAt is not null ? "awarded"
         : a.StartedAt is null ? "unscheduled"
         : "active";

    private static object ItemView(ItemEntity i) => new
    {
        id = i.Id, name = i.Name, description = i.Description, quantity = i.Quantity, holder_id = i.HolderId,
    };

    private static object AuctionView(AuctionEntity a) => new
    {
        id = a.Id, name = a.Name, description = a.Description, item_id = a.ItemId,
        started_at = a.StartedAt, ends_at = a.EndsAt, closed_at = a.ClosedAt,
        awarded_to = a.AwardedTo, winning_bid = a.WinningBid, status = Status(a),
    };

    private static object BidView(BidEntity b) => new
    {
        user_id = b.UserId, auction_id = b.AuctionId, amount = b.Amount, placed_at = b.PlacedAt,
    };
}
