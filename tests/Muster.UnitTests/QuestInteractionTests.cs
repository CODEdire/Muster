using Muster.Bot;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;
using NetCord.Services.ComponentInteractions;
using Xunit;

namespace Muster.UnitTests;

/// <summary>Builds NetCord's component-interaction tree offline (like the slash test) to catch a malformed
/// [ComponentInteraction] route or custom-id parameter binding before it would fail at startup.</summary>
public class QuestInteractionRoutingTests
{
    [Fact]
    public void ButtonInteractions_Register()
    {
        var service = new ComponentInteractionService<ButtonInteractionContext>(
            ComponentInteractionServiceConfiguration<ButtonInteractionContext>.Default);
        service.AddModules(typeof(QuestInteractionModule).Assembly); // throws if a route/param is malformed
        Assert.NotEmpty(service.GetComponentInteractions());
    }

    [Fact]
    public void MenuInteractions_Register()
    {
        var service = new ComponentInteractionService<StringMenuInteractionContext>(
            ComponentInteractionServiceConfiguration<StringMenuInteractionContext>.Default);
        service.AddModules(typeof(QuestInteractionModule).Assembly);
        Assert.NotEmpty(service.GetComponentInteractions());
    }

    [Fact]
    public void ModalInteractions_Register()
    {
        var service = new ComponentInteractionService<ModalInteractionContext>(
            ComponentInteractionServiceConfiguration<ModalInteractionContext>.Default);
        service.AddModules(typeof(QuestInteractionModule).Assembly);
        Assert.NotEmpty(service.GetComponentInteractions());
    }

    [Fact]
    public void SubmitModal_CarriesIdsAndAnInput()
    {
        var questId = Guid.NewGuid();
        var modal = QuestComponentBuilder.BuildSubmitModal(7, questId);
        Assert.Equal($"{QuestComponentBuilder.SubmitModal}:7:{questId}", modal.CustomId);
        Assert.NotEmpty(modal.Components);
    }
}

/// <summary>The component builder is pure — assert which actions surface per phase + audience, and the custom-id scheme.</summary>
public class QuestComponentBuilderTests
{
    private const ulong Guild = 7;
    private static readonly Guid QuestId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static QuestDetailView Quest(QuestStatus status, QuestOrigin origin = QuestOrigin.Guild,
        IReadOnlyList<QuestDetailParticipant>? participants = null) =>
        new(QuestId, "Q", "", origin, status, 10, "COIN", QuestTier.None, 0, 1, 1, "Owner", null,
            null, null, null, DateTimeOffset.UnixEpoch, null, null, participants ?? []);

    [Fact]
    public void CustomId_EncodesGuildQuestAndMember()
    {
        Assert.Equal($"qapprove:{Guild}:{QuestId}:5", QuestComponentBuilder.Id("qapprove", Guild, QuestId, 5));
        Assert.Equal($"qclaim:{Guild}:{QuestId}", QuestComponentBuilder.Id("qclaim", Guild, QuestId));
    }

    [Fact]
    public void Open_PublicCard_HasActions_TerminalHasNone()
    {
        Assert.NotEmpty(QuestComponentBuilder.Build(Quest(QuestStatus.Open), Guild, isMod: false));
        Assert.Empty(QuestComponentBuilder.Build(Quest(QuestStatus.Closed), Guild, isMod: false));
        Assert.Empty(QuestComponentBuilder.Build(Quest(QuestStatus.Scheduled), Guild, isMod: false));
    }

    [Fact]
    public void ModCard_IntakeAndDispute_HaveActions()
    {
        Assert.NotEmpty(QuestComponentBuilder.Build(Quest(QuestStatus.PendingApproval, QuestOrigin.Player), Guild, isMod: true));
        Assert.NotEmpty(QuestComponentBuilder.Build(Quest(QuestStatus.Disputed, QuestOrigin.Player), Guild, isMod: true));
        Assert.NotEmpty(QuestComponentBuilder.Build(Quest(QuestStatus.PendingFinal, QuestOrigin.Player), Guild, isMod: true));
    }

    [Fact]
    public void ModCard_SubmittedGuildQuest_ShowsReviewActions()
    {
        var parts = new List<QuestDetailParticipant>
        {
            new(1, "Bo", null, QuestParticipantStatus.Submitted, null, null, null, null, null),
        };
        Assert.NotEmpty(QuestComponentBuilder.Build(Quest(QuestStatus.Open, participants: parts), Guild, isMod: true));
    }
}
