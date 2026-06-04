using Muster.Bot.Musters.Rendering;
using Muster.Domain.Entities.Musters;
using Muster.Domain.Enums;
using NetCord.Rest;
using Xunit;

namespace Muster.Bot.UnitTests.Musters.Rendering;

/// <summary>
/// The check-in button builder is pure (no Discord/IO) so we can pin the button state per status + capacity without
/// the gateway. Each test states the user-visible rule.
/// </summary>
public class MusterComponentBuilderTests
{
    private static ReactionMuster Muster(
        MusterStatus status = MusterStatus.Open, int? capacity = null, int participants = 0, bool linked = false)
    {
        var m = new ReactionMuster { Id = Guid.NewGuid(), GuildId = 7, Status = status, Capacity = capacity, Prompt = "p" };
        for (var i = 0; i < participants; i++)
        {
            m.Participants.Add(new ReactionParticipant { Id = Guid.NewGuid(), MusterId = m.Id, UserId = (ulong)(i + 1) });
        }

        if (linked)
        {
            m.SessionLinks.Add(new MusterSessionLink { MusterId = m.Id, SessionId = Guid.NewGuid() });
        }

        return m;
    }

    private static ButtonProperties SingleButton(IReadOnlyList<IMessageComponentProperties> rows)
    {
        var row = Assert.IsType<ActionRowProperties>(Assert.Single(rows));
        return Assert.Single(row.Components.OfType<ButtonProperties>());
    }

    [Fact]
    public void Open_BelowCapacity_HasEnabledCheckInButton()
    {
        var button = SingleButton(MusterComponentBuilder.Build(Muster()));
        Assert.NotEqual(true, button.Disabled);
        Assert.Contains("Check in", button.Label);
        Assert.StartsWith(MusterComponentBuilder.CheckIn + ":", button.CustomId);
    }

    [Theory]
    [InlineData(MusterStatus.Closed)]
    [InlineData(MusterStatus.Expired)]
    [InlineData(MusterStatus.Cancelled)]
    public void Terminal_HasNoComponents(MusterStatus status)
    {
        Assert.Empty(MusterComponentBuilder.Build(Muster(status)));
    }

    [Fact]
    public void Locked_HasDisabledButton()
    {
        var button = SingleButton(MusterComponentBuilder.Build(Muster(MusterStatus.Locked)));
        Assert.True(button.Disabled);
    }

    [Fact]
    public void Standalone_AtCapacity_ButtonDisabledAndLabelledFull()
    {
        var button = SingleButton(MusterComponentBuilder.Build(Muster(capacity: 2, participants: 2)));
        Assert.True(button.Disabled);
        Assert.Equal("Full", button.Label);
    }

    [Fact]
    public void Linked_IgnoresCapacity_ButtonStaysEnabled()
    {
        // A linked muster rewards everyone who attended, not the first N — its cap is ignored.
        var button = SingleButton(MusterComponentBuilder.Build(Muster(capacity: 2, participants: 5, linked: true)));
        Assert.NotEqual(true, button.Disabled);
    }
}
