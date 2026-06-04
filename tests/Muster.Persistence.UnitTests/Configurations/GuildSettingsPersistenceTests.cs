using Microsoft.EntityFrameworkCore;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Xunit;

namespace Muster.Persistence.UnitTests.Configurations;

public class GuildSettingsPersistenceTests
{
    [Fact]
    public async Task Settings_RoundTripThroughDb_PreservesNestedQuestSettings()
    {
        using var sqlite = new SqliteDb();
        var db = sqlite.Context;

        db.Guilds.Add(new Guild
        {
            Id = 1,
            Name = "G",
            Settings = new GuildSettings
            {
                LedgerRetentionDays = 21,
                Quests = new QuestSettings
                {
                    MaxRevisions = 3,
                    FinalApprovalMode = FinalApprovalMode.Forced,
                    TierBPoints = 999,
                    SubmissionTimeoutAction = StaleSubmissionAction.Dispute,
                },
            },
        });
        await db.SaveChangesAsync();

        // Re-read from the stored JSON column (not the tracked instance) to prove the owned ToJson mapping persists.
        db.ChangeTracker.Clear();
        var reloaded = await db.Guilds.SingleAsync(g => g.Id == 1);

        Assert.Equal(21, reloaded.Settings.LedgerRetentionDays);
        Assert.Equal(3, reloaded.Settings.Quests.MaxRevisions);
        Assert.Equal(FinalApprovalMode.Forced, reloaded.Settings.Quests.FinalApprovalMode);
        Assert.Equal(999, reloaded.Settings.Quests.TierBPoints);
        Assert.Equal(StaleSubmissionAction.Dispute, reloaded.Settings.Quests.SubmissionTimeoutAction);
    }
}
