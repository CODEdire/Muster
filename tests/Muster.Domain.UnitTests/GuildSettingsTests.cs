using System.Text.Json;
using Muster.Domain.Entities;
using Muster.Domain.Enums;
using Xunit;

namespace Muster.UnitTests;

public class GuildSettingsTests
{
    // Settings is persisted as a whole-document JSON blob. A document written before a property existed
    // must deserialize with that property's C# default, so adding settings never breaks existing guilds.
    [Fact]
    public void LegacySettingsJson_MissingKeys_FallBackToDefaults()
    {
        const string legacy = """{"AdminRoleIds":[],"OfficerRoleIds":[],"QuestManagerRoleIds":[],"ParticipantRoleIds":[],"TrackedChannelIds":[],"QuestsRequireApproval":true,"PointsPerVoiceMinute":1}""";

        var s = JsonSerializer.Deserialize<GuildSettings>(legacy)!;

        // The legacy doc predates the nested Quests object, so it deserializes to QuestSettings defaults.
        Assert.True(s.Quests.PersonalQuestIntakeApproval);          // default on
        Assert.Equal(FinalApprovalMode.OwnerChoice, s.Quests.FinalApprovalMode);
        Assert.Equal(0, s.Quests.IntakeTimeoutHours);               // disabled
        Assert.Equal(0, s.Quests.MaxRevisions);
        Assert.Equal(100, s.Quests.TierSPoints);                    // tier defaults intact
        Assert.Equal(5, s.Quests.TierEPoints);
        Assert.Equal(0, s.Quests.MaxOpenQuestsPerPoster);
    }

    [Fact]
    public void Settings_RoundTrip_PreservesValues()
    {
        var original = new GuildSettings
        {
            LedgerRetentionDays = 30,
            Quests = new QuestSettings
            {
                PersonalQuestIntakeApproval = false,
                FinalApprovalMode = FinalApprovalMode.Forced,
                IntakeTimeoutHours = 12,
                SubmissionTimeoutAction = StaleSubmissionAction.Dispute,
                MaxRevisions = 3,
                TierBPoints = 999,
            },
        };

        var round = JsonSerializer.Deserialize<GuildSettings>(JsonSerializer.Serialize(original))!;

        Assert.False(round.Quests.PersonalQuestIntakeApproval);
        Assert.Equal(FinalApprovalMode.Forced, round.Quests.FinalApprovalMode);
        Assert.Equal(12, round.Quests.IntakeTimeoutHours);
        Assert.Equal(StaleSubmissionAction.Dispute, round.Quests.SubmissionTimeoutAction);
        Assert.Equal(3, round.Quests.MaxRevisions);
        Assert.Equal(999, round.Quests.TierBPoints);
        Assert.Equal(30, round.LedgerRetentionDays);
    }
}
