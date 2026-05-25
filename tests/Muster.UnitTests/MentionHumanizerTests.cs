using Microsoft.EntityFrameworkCore;
using Muster.Infrastructure.Persistence;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Platform;

namespace Muster.UnitTests;

public class MentionHumanizerTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task Humanize_ReplacesRoleAndUserMentions_WithNames()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        await new RoleSyncService(db).UpsertAsync(1, 900, "Officers", permissions: 0);
        await new MemberSyncService(db).UpsertAsync(1, 10, "alice", "Alice", null);
        var sut = new MentionHumanizer(db);

        Assert.Equal("Added @Officers as an officer role.",
            await sut.HumanizeAsync(1, "Added <@&900> as an officer role."));

        Assert.Equal("Awarded 50 points to @Alice — gg",
            await sut.HumanizeAsync(1, "Awarded 50 points to <@10> — gg"));
    }

    [Fact]
    public async Task Humanize_UnknownIds_FallBackToId_AndPlainTextUnchanged()
    {
        using var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        var sut = new MentionHumanizer(db);

        Assert.Equal("role @123", await sut.HumanizeAsync(1, "role <@&123>"));
        Assert.Equal("no mentions here", await sut.HumanizeAsync(1, "no mentions here"));
    }
}
