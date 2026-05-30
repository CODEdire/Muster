using Muster.Bot.Economy;
using Muster.Bot.Questing;
using Muster.Contracts;
using Muster.Domain.Enums;
using Muster.Infrastructure.Services.Quests;
using NetCord.Rest;
using Xunit;

namespace Muster.Bot.UnitTests.Questing.Rendering;

/// <summary>The channel-board embed renderer is pure, so we can assert the card it builds without Discord.</summary>
public class QuestEmbedRendererTests
{
    private const ulong Guild = 99;
    private static readonly Guid QuestId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static QuestDetailView Quest(
        QuestOrigin origin = QuestOrigin.Guild,
        QuestTier tier = QuestTier.B,
        QuestStatus status = QuestStatus.Open,
        string? ownerAvatar = null,
        DateTimeOffset? deadline = null,
        DateTimeOffset? scheduledStart = null,
        ulong? disputedBy = null,
        string? disputeReason = null,
        IReadOnlyList<QuestDetailParticipant>? participants = null) =>
        new(
            Id: QuestId, Name: "Slay the dragon", Description: "Bring back a scale.",
            Origin: origin, Status: status, RewardAmount: 250, RewardCode: "COIN",
            Tier: tier, BonusPoints: 50, Capacity: 3, OwnerId: 7, OwnerName: "Aria", OwnerAvatar: ownerAvatar,
            DisputedBy: disputedBy, DisputedByName: disputedBy is null ? null : "Disputer", DisputeReason: disputeReason,
            CreatedAt: DateTimeOffset.UnixEpoch, ScheduledStart: scheduledStart, Deadline: deadline,
            Participants: participants ?? []);

    private static EmbedFieldProperties Field(NetCord.Rest.EmbedProperties e, string name) =>
        Assert.Single(e.Fields!, f => f.Name == name);

    [Fact]
    public void Render_RewardIsOwnLine_CoinThenTierPoints()
    {
        var reward = Field(QuestEmbedRenderer.Render(Quest(), Guild), "Reward");
        Assert.False(reward.Inline);
        Assert.Equal("🪙 250 COIN\n✨ +50 POINTS", reward.Value);
    }

    [Fact]
    public void Render_NoTier_RewardHasNoPointsLine_AndNoAuthor()
    {
        var embed = QuestEmbedRenderer.Render(Quest(tier: QuestTier.None), Guild);
        Assert.Equal("🪙 250 COIN", Field(embed, "Reward").Value);
        Assert.Null(embed.Author);
    }

    [Fact]
    public void Render_Status_OpenVsInProgress()
    {
        Assert.Equal("🟢 Open", Field(QuestEmbedRenderer.Render(Quest(), Guild), "Status").Value);

        var working = Quest(participants: [new(1, "Bo", null, QuestParticipantStatus.Claimed, null, null, null, null, null)]);
        Assert.Equal("🟡 In progress", Field(QuestEmbedRenderer.Render(working, Guild), "Status").Value);
    }

    [Fact]
    public void Render_Participants_UseMentionsAndExcludeReleased()
    {
        var parts = new List<QuestDetailParticipant>
        {
            new(1, "Bo", null, QuestParticipantStatus.Submitted, null, null, null, null, null),
            new(2, "Cy", null, QuestParticipantStatus.Released, null, null, null, null, null),
        };
        var roster = Field(QuestEmbedRenderer.Render(Quest(participants: parts), Guild), "Participants (1)");
        Assert.Contains("<@1>", roster.Value);
        Assert.DoesNotContain("<@2>", roster.Value);
    }

    [Fact]
    public void Render_Participant_ShowsReviewerWhenReviewed()
    {
        var parts = new List<QuestDetailParticipant>
        {
            new(1, "Bo", null, QuestParticipantStatus.Approved, null, "ok", ReviewedBy: 9, "Mgr", null),
        };
        var roster = Field(QuestEmbedRenderer.Render(Quest(participants: parts), Guild), "Participants (1)");
        Assert.Contains("<@1>", roster.Value);
        Assert.Contains("by <@9>", roster.Value);
    }

    [Fact]
    public void Render_SlotsAndCompletions()
    {
        var parts = new List<QuestDetailParticipant>
        {
            new(1, "Bo", null, QuestParticipantStatus.Approved, null, null, null, null, null),
            new(2, "Cy", null, QuestParticipantStatus.Claimed, null, null, null, null, null),
        };
        var embed = QuestEmbedRenderer.Render(Quest(participants: parts), Guild);
        Assert.Equal("2/3", Field(embed, "Slots").Value);
        Assert.Equal("✅ 1", Field(embed, "Completions").Value);
    }

    [Fact]
    public void Render_Expires_UsesRelativeTimestamp()
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        var expires = Field(QuestEmbedRenderer.Render(Quest(deadline: d), Guild), "Expires");
        Assert.Equal("<t:1900000000:R>", expires.Value);
    }

    [Fact]
    public void Render_Scheduled_ShowsOpens()
    {
        var s = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        var embed = QuestEmbedRenderer.Render(Quest(status: QuestStatus.Scheduled, scheduledStart: s), Guild);
        Assert.Equal("<t:1900000000:R>", Field(embed, "Opens").Value);
    }

    [Fact]
    public void Render_Disputed_ShowsDisputerAndReason()
    {
        var embed = QuestEmbedRenderer.Render(Quest(status: QuestStatus.Disputed, disputedBy: 42, disputeReason: "Work was incomplete"), Guild);
        Assert.Equal("<@42>", Field(embed, "Disputed by").Value);
        Assert.Equal("⚔️ Disputed", Field(embed, "Status").Value);
        Assert.Equal("Work was incomplete", Field(embed, "Dispute reason").Value);
    }

    [Fact]
    public void Render_Participant_ShowsSubmissionAndReviewNotes()
    {
        var parts = new List<QuestDetailParticipant>
        {
            new(1, "Bo", null, QuestParticipantStatus.Submitted, "did the thing", null, null, null, null),
            new(2, "Cy", null, QuestParticipantStatus.RevisionRequested, "attempt", "needs more detail", ReviewedBy: 9, "Mgr", null),
        };
        var roster = Field(QuestEmbedRenderer.Render(Quest(participants: parts), Guild), "Participants (2)").Value;
        Assert.Contains("did the thing", roster);      // submission note
        Assert.Contains("needs more detail", roster);   // reviewer feedback wins over the worker's note
        Assert.DoesNotContain("attempt", roster);
    }

    [Fact]
    public void Render_GuildFooter_WithBaseUrl_UsesShieldCrest()
    {
        var embed = QuestEmbedRenderer.Render(Quest(origin: QuestOrigin.Guild), Guild, "https://muster.example");
        Assert.Equal("Guild issued", embed.Footer!.Text);
        Assert.Equal("https://muster.example/img/tiers/guild.png", embed.Footer.IconUrl);
    }

    [Fact]
    public void Render_PlayerFooter_UsesOwnerAvatar()
    {
        var embed = QuestEmbedRenderer.Render(Quest(origin: QuestOrigin.Player, ownerAvatar: "https://cdn/avatar.png"), Guild);
        Assert.Equal("Aria", embed.Footer!.Text);
        Assert.Equal("https://cdn/avatar.png", embed.Footer.IconUrl);
    }

    [Fact]
    public void Render_WithBaseUrl_LinksTitleAndTierBadge()
    {
        var embed = QuestEmbedRenderer.Render(Quest(tier: QuestTier.B), Guild, "https://muster.example/");
        Assert.Equal($"https://muster.example/guilds/{Guild}/quests/{QuestId}", embed.Url);
        Assert.Equal("https://muster.example/img/tiers/b.png", embed.Author!.IconUrl);
    }
}
