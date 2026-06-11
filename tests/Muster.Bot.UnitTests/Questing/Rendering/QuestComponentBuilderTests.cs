using Muster.Bot.Questing;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;
using NetCord;
using NetCord.Rest;
using Xunit;

namespace Muster.Bot.UnitTests.Questing.Rendering;

/// <summary>
/// The component builder is pure (no Discord/IO) so we can assert which actions surface per phase + audience
/// + participant state without touching the gateway. Each test states the user-visible rule it pins.
/// </summary>
public class QuestComponentBuilderTests
{
    private const ulong Guild = 7;
    private const ulong Owner = 100;
    private const ulong Claimer = 200;
    private const ulong Other = 300;
    private static readonly Guid QuestId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static QuestDetailView Quest(
        QuestStatus status,
        QuestOrigin origin = QuestOrigin.Guild,
        int capacity = 1,
        ulong ownerId = Owner,
        IReadOnlyList<QuestDetailParticipant>? participants = null) =>
        new(QuestId, "Q", "", origin, status, 10, "COIN", QuestTier.None, 0, capacity,
            ownerId, "Owner", null, null, null, null,
            DateTimeOffset.UnixEpoch, null, null, null, null, null, participants ?? []);

    private static QuestDetailParticipant Part(ulong userId, QuestParticipantStatus status) =>
        new(userId, $"User{userId}", null, status, null, null, null, null, null);

    private static ActionRowProperties Row(IMessageComponentProperties row)
    {
        Assert.IsType<ActionRowProperties>(row);
        return (ActionRowProperties)row;
    }

    // ------- custom_id encoding -----------------------------------------------------------------

    [Fact]
    public void CustomId_WithMember_EncodesAllFour()
    {
        Assert.Equal($"qapprove:{Guild}:{QuestId}:5", QuestComponentBuilder.Id("qapprove", Guild, QuestId, 5));
    }

    [Fact]
    public void CustomId_WithoutMember_OmitsMemberSegment()
    {
        Assert.Equal($"qclaim:{Guild}:{QuestId}", QuestComponentBuilder.Id("qclaim", Guild, QuestId));
    }

    // ------- public board: actions vs no-actions ------------------------------------------------

    [Fact]
    public void Open_PublicCard_HasActions()
    {
        Assert.NotEmpty(QuestComponentBuilder.Build(Quest(QuestStatus.Open), Guild, isMod: false));
    }

    [Theory]
    [InlineData(QuestStatus.Closed)]
    [InlineData(QuestStatus.Cancelled)]
    [InlineData(QuestStatus.Expired)]
    [InlineData(QuestStatus.Scheduled)]
    public void NonOpenPublic_HasNoActions(QuestStatus status)
    {
        Assert.Empty(QuestComponentBuilder.Build(Quest(status), Guild, isMod: false));
    }

    // ------- public board: claim button availability vs capacity --------------------------------

    [Fact]
    public void OpenPublic_BelowCapacity_ShowsClaimButton()
    {
        var rows = QuestComponentBuilder.Build(Quest(QuestStatus.Open, capacity: 3, participants:
            [Part(1, QuestParticipantStatus.Claimed)]), Guild, isMod: false);

        var row = Row(Assert.Single(rows));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Claim + ":"));
    }

    [Fact]
    public void OpenPublic_AtCapacity_HidesClaimButton_KeepsMyActions()
    {
        var participants = new[]
        {
            Part(1, QuestParticipantStatus.Claimed),
            Part(2, QuestParticipantStatus.Submitted),
        };
        var rows = QuestComponentBuilder.Build(Quest(QuestStatus.Open, capacity: 2, participants: participants), Guild, isMod: false);

        var row = Row(Assert.Single(rows));
        Assert.DoesNotContain(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Claim + ":"));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Mine + ":"));
    }

    // ------- mod card: phase-specific surfaces --------------------------------------------------

    [Theory]
    [InlineData(QuestStatus.PendingApproval)]
    [InlineData(QuestStatus.Disputed)]
    [InlineData(QuestStatus.PendingFinal)]
    public void ModCard_ReviewablePhases_HaveActions(QuestStatus status)
    {
        Assert.NotEmpty(QuestComponentBuilder.Build(Quest(status, QuestOrigin.Player), Guild, isMod: true));
    }

    [Fact]
    public void ModCard_SingleSubmitter_ShowsApproveRejectReviseButtons()
    {
        var rows = QuestComponentBuilder.Build(Quest(QuestStatus.Open, participants:
            [Part(1, QuestParticipantStatus.Submitted)]), Guild, isMod: true);

        var row = Row(Assert.Single(rows));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Approve + ":"));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Reject + ":"));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.ReviseMember + ":"));
    }

    [Fact]
    public void ModCard_MultipleSubmitters_ShowsPickerMenu()
    {
        var parts = new[]
        {
            Part(1, QuestParticipantStatus.Submitted),
            Part(2, QuestParticipantStatus.Submitted),
            Part(3, QuestParticipantStatus.Submitted),
        };
        var rows = QuestComponentBuilder.Build(Quest(QuestStatus.Open, participants: parts), Guild, isMod: true);

        var single = Assert.Single(rows);
        Assert.IsType<StringMenuProperties>(single);
        Assert.StartsWith(QuestComponentBuilder.ReviewPick + ":", ((StringMenuProperties)single).CustomId);
    }

    // ------- DM actions: owner vs claimer vs bystander ------------------------------------------

    [Fact]
    public void Dm_Claimer_HasSubmitAndAbandon()
    {
        var parts = new[] { Part(Claimer, QuestParticipantStatus.Claimed) };
        var rows = QuestComponentBuilder.DmActions(Quest(QuestStatus.Open, participants: parts), Guild, Claimer);

        var row = Row(Assert.Single(rows));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Submit + ":"));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Abandon + ":"));
    }

    [Fact]
    public void Dm_RevisionRequestedClaimer_StillHasSubmit()
    {
        var parts = new[] { Part(Claimer, QuestParticipantStatus.RevisionRequested) };
        var rows = QuestComponentBuilder.DmActions(Quest(QuestStatus.Open, participants: parts), Guild, Claimer);

        var row = Row(Assert.Single(rows));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Submit + ":"));
    }

    [Fact]
    public void Dm_PlayerOwner_AfterSubmission_HasConfirmReviseDispute()
    {
        var parts = new[] { Part(Claimer, QuestParticipantStatus.Submitted) };
        var rows = QuestComponentBuilder.DmActions(
            Quest(QuestStatus.Open, QuestOrigin.Player, participants: parts), Guild, Owner);

        var row = Row(Assert.Single(rows));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Confirm + ":"));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Revise + ":"));
        Assert.Contains(row.Components.OfType<ButtonProperties>(), b => b.CustomId!.StartsWith(QuestComponentBuilder.Dispute + ":"));
    }

    [Fact]
    public void Dm_PlayerOwner_BeforeSubmission_HasCancelOnly()
    {
        var rows = QuestComponentBuilder.DmActions(Quest(QuestStatus.Open, QuestOrigin.Player), Guild, Owner);

        var row = Row(Assert.Single(rows));
        var button = Assert.Single(row.Components.OfType<ButtonProperties>());
        Assert.StartsWith(QuestComponentBuilder.Cancel + ":", button.CustomId);
    }

    [Fact]
    public void Dm_GuildOwner_HasNoActions()
    {
        // Guild origin → owner controls (confirm/dispute/cancel) don't surface; reviews happen via mod channel.
        var rows = QuestComponentBuilder.DmActions(Quest(QuestStatus.Open, QuestOrigin.Guild), Guild, Owner);
        Assert.Empty(rows);
    }

    [Fact]
    public void Dm_Bystander_HasNoActions()
    {
        var parts = new[] { Part(Claimer, QuestParticipantStatus.Claimed) };
        var rows = QuestComponentBuilder.DmActions(Quest(QuestStatus.Open, participants: parts), Guild, Other);
        Assert.Empty(rows);
    }

    // ------- modal helpers ----------------------------------------------------------------------

    [Fact]
    public void NoteModal_WithMember_EncodesMemberInId()
    {
        var modal = QuestComponentBuilder.NoteModal("qrejm", Guild, QuestId, 999, "T", "L", "P");
        Assert.Equal($"qrejm:{Guild}:{QuestId}:999", modal.CustomId);
        Assert.NotEmpty(modal.Components);
    }

    [Fact]
    public void NoteModal_WithoutMember_OmitsMemberFromId()
    {
        var modal = QuestComponentBuilder.NoteModal("qrejm", Guild, QuestId, null, "T", "L", "P");
        Assert.Equal($"qrejm:{Guild}:{QuestId}", modal.CustomId);
    }

    // ------- explicit ReviewButtons API ---------------------------------------------------------

    [Fact]
    public void ReviewButtons_AllThreeButtonsTargetMember()
    {
        var row = QuestComponentBuilder.ReviewButtons(Guild, QuestId, 42);
        Assert.Equal(3, row.Components.OfType<ButtonProperties>().Count());
        Assert.All(row.Components.OfType<ButtonProperties>(), b => Assert.EndsWith(":42", b.CustomId!));
    }
}
