using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;

namespace Muster.IntegrationTests;

public class AuditTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        await new MemberSyncService(db).UpsertAsync(1, 5, "officer", "Officer", null);
        return db;
    }

    [Fact]
    public async Task Record_ThenSearch_ResolvesActorName()
    {
        using var db = await SeededAsync();
        var sut = new AuditService(db);

        await sut.RecordAsync(1, 5, "award.points", "50 to <@10>");

        var page = await sut.SearchAsync(1, new AuditQuery());
        var entry = Assert.Single(page.Items);
        Assert.Equal("award.points", entry.Action);
        Assert.Equal("Officer", entry.ActorName);
    }

    [Fact]
    public async Task Search_FiltersByActionAndText()
    {
        using var db = await SeededAsync();
        var sut = new AuditService(db);
        await sut.RecordAsync(1, 5, "award.points", "to alice");
        await sut.RecordAsync(1, 5, "season.start", "Season 2");
        await sut.RecordAsync(1, 5, "config.adminRole", "added");

        Assert.Equal(1, (await sut.SearchAsync(1, new AuditQuery(Action: "season.start"))).Total);
        Assert.Equal(1, (await sut.SearchAsync(1, new AuditQuery(Search: "alice"))).Total);
        Assert.Equal(3, (await sut.SearchAsync(1, new AuditQuery())).Total);

        var actions = await sut.GetActionsAsync(1);
        Assert.Equal(3, actions.Count);
    }

    [Fact]
    public async Task Search_PagesResults()
    {
        using var db = await SeededAsync();
        var sut = new AuditService(db);
        for (var i = 0; i < 7; i++)
        {
            await sut.RecordAsync(1, 5, "award.points", $"entry {i}");
        }

        var p1 = await sut.SearchAsync(1, new AuditQuery(Page: 1, PageSize: 3));
        Assert.Equal(7, p1.Total);
        Assert.Equal(3, p1.Items.Count);
        Assert.Equal(3, p1.TotalPages);

        var p3 = await sut.SearchAsync(1, new AuditQuery(Page: 3, PageSize: 3));
        Assert.Single(p3.Items); // 7 = 3 + 3 + 1
    }

    [Fact]
    public async Task Export_ReturnsAllMatching_IgnoringPaging()
    {
        using var db = await SeededAsync();
        var sut = new AuditService(db);
        for (var i = 0; i < 5; i++)
        {
            await sut.RecordAsync(1, 5, "award.points", $"e{i}");
        }

        var exported = await sut.ExportAsync(1, new AuditQuery(PageSize: 2));
        Assert.Equal(5, exported.Count);
    }
}
