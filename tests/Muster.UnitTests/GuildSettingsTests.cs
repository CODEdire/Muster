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

        Assert.True(s.PersonalQuestIntakeApproval);                 // default on
        Assert.Equal(FinalApprovalMode.OwnerChoice, s.FinalApprovalMode);
        Assert.Equal(0, s.IntakeTimeoutHours);                      // disabled
        Assert.Equal(0, s.MaxRevisions);
        Assert.Equal(100, s.TierSPoints);                           // tier defaults intact
        Assert.Equal(5, s.TierEPoints);
        Assert.Equal(0, s.MaxOpenQuestsPerPoster);
    }

    [Fact]
    public void Settings_RoundTrip_PreservesValues()
    {
        var original = new GuildSettings
        {
            PersonalQuestIntakeApproval = false,
            FinalApprovalMode = FinalApprovalMode.Forced,
            IntakeTimeoutHours = 12,
            SubmissionTimeoutAction = StaleSubmissionAction.Dispute,
            MaxRevisions = 3,
            TierBPoints = 999,
            QuestManagerRoleIds = [1, 2, 3],
        };

        var round = JsonSerializer.Deserialize<GuildSettings>(JsonSerializer.Serialize(original))!;

        Assert.False(round.PersonalQuestIntakeApproval);
        Assert.Equal(FinalApprovalMode.Forced, round.FinalApprovalMode);
        Assert.Equal(12, round.IntakeTimeoutHours);
        Assert.Equal(StaleSubmissionAction.Dispute, round.SubmissionTimeoutAction);
        Assert.Equal(3, round.MaxRevisions);
        Assert.Equal(999, round.TierBPoints);
        Assert.Equal([1ul, 2ul, 3ul], round.QuestManagerRoleIds);
    }
}
