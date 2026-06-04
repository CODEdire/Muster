using Muster.Bot.Musters.Rendering;
using Muster.Domain.Entities.Musters;
using Muster.Domain.Enums;
using NetCord.Rest;
using Xunit;

namespace Muster.Bot.UnitTests.Musters.Rendering;

/// <summary>The muster card embed renderer is pure, so we can assert the card it builds without Discord.</summary>
public class MusterEmbedRendererTests
{
    private static ReactionMuster Muster(
        MusterStatus status = MusterStatus.Open, int? capacity = null, int participants = 0,
        long points = 0, long coins = 0, Guid? coinCcy = null, int? minCheckIns = null,
        DateTimeOffset? expiresAt = null, string? title = null)
    {
        var m = new ReactionMuster
        {
            Id = Guid.NewGuid(), GuildId = 7, Status = status, Capacity = capacity, Title = title, Prompt = "Roll call",
            Points = points, Coins = coins, CoinCurrencyId = coinCcy, MinCheckIns = minCheckIns, ExpiresAt = expiresAt,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        for (var i = 0; i < participants; i++)
        {
            m.Participants.Add(new ReactionParticipant { Id = Guid.NewGuid(), MusterId = m.Id, UserId = (ulong)(i + 1), CheckedInAt = DateTimeOffset.UnixEpoch });
        }

        return m;
    }

    private static EmbedFieldProperties? Field(EmbedProperties e, string name) => e.Fields!.SingleOrDefault(f => f.Name == name);

    [Theory]
    [InlineData(MusterStatus.Open, "Open")]
    [InlineData(MusterStatus.Locked, "Locked")]
    [InlineData(MusterStatus.Closed, "Closed")]
    [InlineData(MusterStatus.Expired, "Expired")]
    public void Status_Field_LabelsTheStatus(MusterStatus status, string expected)
    {
        var embed = MusterEmbedRenderer.Render(Muster(status), null);
        Assert.Contains(expected, Field(embed, "Status")!.Value);
    }

    [Fact]
    public void Reward_Field_ShownWithPointsAndCoinCode()
    {
        var embed = MusterEmbedRenderer.Render(Muster(points: 10, coins: 5, coinCcy: Guid.NewGuid()), "RSI");
        var reward = Field(embed, "Reward")!.Value;
        Assert.Contains("10 pts", reward);
        Assert.Contains("5 RSI", reward);
    }

    [Fact]
    public void Reward_Field_AbsentWhenNoReward()
    {
        Assert.Null(Field(MusterEmbedRenderer.Render(Muster(), null), "Reward"));
    }

    [Fact]
    public void Checked_In_ShowsCapacity_ForStandaloneCapped()
    {
        var embed = MusterEmbedRenderer.Render(Muster(capacity: 5, participants: 2), null);
        Assert.Contains("2/5", Field(embed, "Checked in")!.Value);
    }

    [Fact]
    public void MinToReward_Field_OnlyWhenSet_AndShowsMetState()
    {
        Assert.Null(Field(MusterEmbedRenderer.Render(Muster(), null), "Min to reward"));

        var below = MusterEmbedRenderer.Render(Muster(participants: 1, minCheckIns: 3), null);
        Assert.Contains("1/3", Field(below, "Min to reward")!.Value);
        Assert.Contains("⚠️", Field(below, "Min to reward")!.Value);

        var met = MusterEmbedRenderer.Render(Muster(participants: 3, minCheckIns: 3), null);
        Assert.Contains("✅", Field(met, "Min to reward")!.Value);
    }

    [Fact]
    public void MinToReward_Field_AbsentWhenMinimumIsZero()
    {
        // 0 is an always-met minimum — no point showing the bar.
        Assert.Null(Field(MusterEmbedRenderer.Render(Muster(minCheckIns: 0), null), "Min to reward"));
    }

    [Fact]
    public void Title_FallsBackWhenNotSet()
    {
        Assert.Contains("check in", MusterEmbedRenderer.Render(Muster(), null).Title!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Strike", MusterEmbedRenderer.Render(Muster(title: "Strike"), null).Title!);
    }
}
