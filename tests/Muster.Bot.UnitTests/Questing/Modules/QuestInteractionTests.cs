using Muster.Bot.Questing;
using NetCord.Services.ComponentInteractions;
using Xunit;

namespace Muster.Bot.UnitTests.Questing.Modules;

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
