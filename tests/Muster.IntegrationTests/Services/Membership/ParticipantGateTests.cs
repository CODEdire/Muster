using Muster.Contracts;
using Muster.IntegrationTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Muster.Persistence;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Muster.Infrastructure;
using Xunit;
using Muster.Infrastructure.Services.Currencies;
using Muster.Infrastructure.Services.Membership;
using Muster.Infrastructure.Services.Musters;
using Muster.Infrastructure.Services.Quests;

namespace Muster.IntegrationTests;

public class ParticipantGateTests
{
    private static MusterDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusterDbContext>()
            .UseInMemoryDatabase($"muster-{Guid.NewGuid()}")
            .Options);

    private static async Task<MusterDbContext> SeededAsync()
    {
        var db = NewDb();
        await new GuildProvisioningService(db).EnsureGuildAsync(1, "G", null, ownerId: 1);
        return db;
    }

    [Fact]
    public async Task Participation_OpenByDefault_WhenNoRolesConfigured()
    {
        using var db = await SeededAsync();
        await new MemberSyncService(db).UpsertAsync(1, 10, "guest", null, null, roleIds: [123]);

        var auth = new GuildAuthorizationService(db);
        Assert.True(await auth.IsParticipantAsync(1, 10));     // member
        Assert.True(await auth.IsParticipantAsync(1, 999));    // even unknown user, since open
    }

    [Fact]
    public async Task Participation_RestrictedToConfiguredRoles()
    {
        using var db = await SeededAsync();
        var guild = await db.Guilds.SingleAsync();
        guild.Settings.ParticipantRoleIds = [500]; // org member role
        await db.SaveChangesAsync();

        var sync = new MemberSyncService(db);
        await sync.UpsertAsync(1, 10, "member", null, null, roleIds: [500]);
        await sync.UpsertAsync(1, 20, "guest", null, null, roleIds: [123]);

        var auth = new GuildAuthorizationService(db);
        Assert.True(await auth.IsParticipantAsync(1, 10));   // holds participant role
        Assert.False(await auth.IsParticipantAsync(1, 20));  // guest excluded
        Assert.False(await auth.IsParticipantAsync(1, 999)); // unknown member excluded
    }

    [Fact]
    public async Task IneligibleMember_CannotEarnFromMuster_OrClaimQuest()
    {
        using var db = await SeededAsync();
        var guild = await db.Guilds.SingleAsync();
        guild.Settings.ParticipantRoleIds = [500];
        await db.SaveChangesAsync();

        var points = await db.Currencies.SingleAsync(c => c.Code == "POINTS");
        var auth = new GuildAuthorizationService(db);
        var awards = new CurrencyService(db, new RecordingMessageBus());
        await new MemberSyncService(db).UpsertAsync(1, 20, "guest", null, null, roleIds: [123]);

        // Muster reaction by a guest -> not eligible, no reward.
        var musters = new MusterService(db, awards, auth);
        await musters.CreateAsync(1, 100, 999, "Roll call", ["✅"], points.Id, 10, capacity: null, expiresAt: null);
        Assert.Equal(ReactionOutcome.NotEligible, await musters.RecordReactionAsync(999, 20, "✅"));
        Assert.Equal(0, await db.CurrencyLedgerEntries.CountAsync());

        // Quest claim by a guest -> rejected.
        var quests = new QuestService(db, awards, auth, new RecordingMessageBus());
        var quest = (await quests.PostQuestAsync(new QuestDraft(1, 1, QuestOrigin.Guild, "Recruit", "desc", points.Id, 50))).Quest!;
        Assert.Equal(QuestResult.NotEligible, await quests.ClaimAsync(quest.Id, 20));
    }
}
