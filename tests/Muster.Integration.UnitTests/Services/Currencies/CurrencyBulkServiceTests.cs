using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muster.Contracts;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Muster.Infrastructure.Messaging;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Web;
using Muster.Persistence;
using Muster.Persistence.Queries;
using Muster.IntegrationTests.TestSupport;
using Xunit;

namespace Muster.IntegrationTests;

public class CurrencyBulkServiceTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    // Provision a guild where role 800 = economy manager; seed a manager (10) + two plain members (20, 30) holding role 700.
    private static async Task<MusterDbContext> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 999);
        await db.MapRoleAsync(1, 800, GuildRoleTier.EconomyManager);

        var sync = new MemberSyncService(db);
        await sync.UpsertAsync(1, 10, "manager", null, null, roleIds: [800]);
        await sync.UpsertAsync(1, 20, "alice", "Alice", null, roleIds: [700]);
        await sync.UpsertAsync(1, 30, "bob", "Bob", null, roleIds: [700]);
        return db;
    }

    private static CurrencyBulkService Sut(MusterDbContext db, out RecordingMessageBus bus)
    {
        bus = new RecordingMessageBus();
        return new CurrencyBulkService(db, new CurrencyAuthorizer(new GuildAuthorizationService(db)), bus);
    }

    private static async Task RunQueuedAsync(MusterDbContext db, RecordingMessageBus bus)
    {
        // Drain the published RunCurrencyBulkAdjust messages through the handler (what the worker does).
        var currency = new CurrencyService(db, new RecordingMessageBus());
        foreach (var run in bus.Published.OfType<RunCurrencyBulkAdjust>().ToList())
        {
            await RunCurrencyBulkAdjustHandler.Handle(run, db, currency, NullLogger<RunCurrencyBulkAdjust>.Instance, default);
        }
    }

    [Fact]
    public async Task Queue_Forbidden_ForNonStaff()
    {
        using var db = await SeededAsync();
        var sut = Sut(db, out _);

        var result = await sut.QueueAsync(1, actorId: 20, "POINTS", 100, "event", [20, 30]); // 20 is a plain member

        Assert.False(result.Ok);
        Assert.Empty(db.CurrencyBulkBatches);
    }

    [Fact]
    public async Task Queue_PersistsBatch_Audits_AndPublishes()
    {
        using var db = await SeededAsync();
        var sut = Sut(db, out var bus);

        var result = await sut.QueueAsync(1, actorId: 10, "POINTS", 100, "event payout", [20, 30]);

        Assert.True(result.Ok);
        Assert.NotNull(result.BatchId);
        var batch = await db.CurrencyBulkBatches.SingleAsync();
        Assert.Equal(2, batch.TotalCount);
        Assert.Equal(BulkBatchStatus.Pending, batch.Status);
        Assert.Contains(bus.Published, m => m is RunCurrencyBulkAdjust);
        Assert.Contains(db.AuditLogs, a => a.Action == "currency.bulkMint");
    }

    [Fact]
    public async Task Run_AppliesToAllTargets_AndIsIdempotentOnRerun()
    {
        using var db = await SeededAsync();
        var sut = Sut(db, out var bus);
        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");

        await sut.QueueAsync(1, actorId: 10, "POINTS", 100, "payout", [20, 30]);
        await RunQueuedAsync(db, bus);

        var season = await db.ActiveSeasonIdAsync(1); // POINTS is seasonal — entries land in the active season
        var batch = await db.CurrencyBulkBatches.SingleAsync();
        Assert.Equal(BulkBatchStatus.Completed, batch.Status);
        Assert.Equal(2, batch.AppliedCount);
        Assert.Equal(100, await db.BalanceAsync(1, 20, points.Id, season));
        Assert.Equal(100, await db.BalanceAsync(1, 30, points.Id, season));

        // Re-running the same job must not double-apply (ledger dedupes on bulk:{id}:{user}).
        await RunCurrencyBulkAdjustHandler.Handle(
            new RunCurrencyBulkAdjust(batch.Id), db, new CurrencyService(db, new RecordingMessageBus()), NullLogger<RunCurrencyBulkAdjust>.Instance, default);
        Assert.Equal(100, await db.BalanceAsync(1, 20, points.Id, season));
    }

    [Fact]
    public async Task RoleTargeting_ResolvesMembersHoldingTheRole()
    {
        using var db = await SeededAsync();
        var members = new WebMemberService(db, new CurrencyReadService(db));

        var targets = await members.GetRoleTargetsAsync(1, 700);

        Assert.Equal(2, targets.Count);
        Assert.Contains(20ul, targets);
        Assert.Contains(30ul, targets);
        Assert.DoesNotContain(10ul, targets); // officer doesn't hold role 700
    }

    [Fact]
    public async Task Roster_ListsMembersWithBalances_AndFiltersByName()
    {
        using var db = await SeededAsync();
        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        await new CurrencyService(db, new RecordingMessageBus())
            .AwardAsync(1, 20, points.Id, 250, CurrencyLedgerSource.ManualAward, "s", "r");
        var members = new WebMemberService(db, new CurrencyReadService(db));

        var all = await members.GetRosterAsync(1, "POINTS", search: null, skip: 0, take: 50);
        Assert.Equal(3, all.Total); // officer + alice + bob (humans)
        Assert.Equal(250, all.Rows.First(r => r.UserId == 20).Balance);
        Assert.Equal(0, all.Rows.First(r => r.UserId == 30).Balance); // no wallet → defaulted

        var filtered = await members.GetRosterAsync(1, "POINTS", search: "ali", skip: 0, take: 50);
        Assert.Single(filtered.Rows);
        Assert.Equal(20ul, filtered.Rows[0].UserId);
    }
}
